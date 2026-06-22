# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "pdfplumber",
# ]
# ///

import pdfplumber

def segment_words_into_blocks(line_words_list) -> str:
    """
    1. Divise les lignes physiques en segments horizontaux s'il y a de grands espaces.
    2. Aligne et fusionne ces segments verticalement pour créer des blocs.
    3. Reconstruit les paragraphes au sein de chaque bloc (fusion des lignes coupées).
    """
    if not line_words_list:
        return ""

    # --- ÉTAPE 1 : Regroupement par ligne physique ---
    lines_dict = {}
    for w in line_words_list:
        line_key = round(w["top"] / 4)
        lines_dict.setdefault(line_key, []).append(w)

    # --- ÉTAPE 2 : Détection des segments horizontaux ---
    all_segments = []
    for key in sorted(lines_dict.keys()):
        words_in_line = sorted(lines_dict[key], key=lambda w: w["x0"])
        if not words_in_line:
            continue

        current_text = words_in_line[0]["text"]
        seg_x0 = words_in_line[0]["x0"]
        seg_x1 = words_in_line[0]["x1"]
        seg_top = words_in_line[0]["top"]
        seg_bottom = words_in_line[0]["bottom"]

        for i in range(1, len(words_in_line)):
            prev = words_in_line[i-1]
            curr = words_in_line[i]
            gap = curr["x0"] - prev["x1"]

            if gap < 6:
                current_text += curr["text"]
                seg_x1 = max(seg_x1, curr["x1"])
                seg_top = min(seg_top, curr["top"])
                seg_bottom = max(seg_bottom, curr["bottom"])
            elif gap < 15:
                current_text += " " + curr["text"]
                seg_x1 = max(seg_x1, curr["x1"])
                seg_top = min(seg_top, curr["top"])
                seg_bottom = max(seg_bottom, curr["bottom"])
            else:
                all_segments.append({
                    "text": current_text, "x0": seg_x0, "x1": seg_x1, "top": seg_top, "bottom": seg_bottom
                })
                current_text = curr["text"]
                seg_x0 = curr["x0"]
                seg_x1 = curr["x1"]
                seg_top = curr["top"]
                seg_bottom = curr["bottom"]

        all_segments.append({
            "text": current_text, "x0": seg_x0, "x1": seg_x1, "top": seg_top, "bottom": seg_bottom
        })

    all_segments.sort(key=lambda s: s["top"])

    # --- ÉTAPE 3 : Clustering 2D (Assemblage en blocs) ---
    blocks = []
    for seg in all_segments:
        matched_block = None
        for block in blocks:
            block_bottom = max(s["bottom"] for s in block)
            block_x0 = min(s["x0"] for s in block)
            block_x1 = max(s["x1"] for s in block)

            v_gap = seg["top"] - block_bottom
            h_overlap = not (seg["x1"] < block_x0 - 15 or seg["x0"] > block_x1 + 15)

            if 0 <= v_gap <= 12 and h_overlap:
                matched_block = block
                break

        if matched_block:
            matched_block.append(seg)
        else:
            blocks.append([seg])

    # --- ÉTAPE 4 : Reconstruction intelligente des paragraphes ---
    formatted_blocks = []
    for block in blocks:
        block_lines = {}
        for s in block:
            l_key = round(s["top"] / 4)
            block_lines.setdefault(l_key, []).append(s)

        lines_str = []
        for l_key in sorted(block_lines.keys()):
            sorted_segs = sorted(block_lines[l_key], key=lambda s: s["x0"])
            lines_str.append(" ".join(s["text"] for s in sorted_segs).strip())

        if not lines_str:
            continue

        # Algorithme de fusion des lignes en paragraphes
        paragraphs = []
        current_sentence = lines_str[0]

        for i in range(1, len(lines_str)):
            next_line = lines_str[i]
            if not next_line:
                continue

            last_char = current_sentence[-1] if current_sentence else ""
            first_char = next_line[0] if next_line else ""

            # CONDITION DE FUSION :
            # Pas de ponctuation forte en fin de ligne ET la ligne suivante commence par une minuscule
            if last_char not in ['.', '!', '?', ':', ';'] and first_char.islower():
                current_sentence += " " + next_line
            else:
                # C'est un vrai saut de paragraphe ou un élément de liste distinct
                paragraphs.append(current_sentence)
                current_sentence = next_line

        paragraphs.append(current_sentence)
        formatted_blocks.append("\n".join(paragraphs))

    return "\n\n".join([f"[BLOC]\n{b}" for b in formatted_blocks if b.strip()])


def detect_all_column_splits(words, page_width: float, page_height: float) -> list[float]:
    """Détecte les colonnes verticales majeures sur la zone centrale."""
    if not words:
        return []

    middle_words = [w for w in words if page_height * 0.15 < w["top"] < page_height * 0.85]
    if not middle_words:
        middle_words = words

    hist = [0] * int(page_width + 1)
    for w in middle_words:
        start = max(0, int(w["x0"]))
        end = min(int(page_width), int(w["x1"]))
        for x in range(start, end + 1):
            hist[x] += 1

    start_search = int(page_width * 0.10)
    end_search = int(page_width * 0.90)

    splits = []
    in_gutter = False
    gutter_start = 0
    min_density = min(hist[start_search:end_search])
    threshold = max(min_density, 1)

    for x in range(start_search, end_search):
        if hist[x] <= threshold:
            if not in_gutter:
                in_gutter = True
                gutter_start = x
        else:
            if in_gutter:
                in_gutter = False
                gutter_len = x - gutter_start
                if gutter_len >= 12:
                    splits.append(gutter_start + (gutter_len / 2))

    if in_gutter and (end_search - gutter_start) >= 12:
        splits.append(gutter_start + ((end_search - gutter_start) / 2))

    cleaned_splits = []
    if splits:
        splits.sort()
        cleaned_splits.append(splits[0])
        for s in splits[1:]:
            if s - cleaned_splits[-1] > 40:
                cleaned_splits.append(s)

    return cleaned_splits


def extract_text_structured_layout(pdf_path: str) -> str:
    pages_text = []
    CHARS_TO_IGNORE = {'', ''}

    with pdfplumber.open(pdf_path) as pdf:
        for idx, page in enumerate(pdf.pages, start=1):
            raw_words = page.extract_words(
                keep_blank_chars=True,
                use_text_flow=True,
                horizontal_ltr=True,
                x_tolerance=3,
                y_tolerance=3
            )

            if not raw_words:
                continue

            words = []
            for w in raw_words:
                cleaned_text = "".join(c for c in w["text"] if c not in CHARS_TO_IGNORE).strip()
                if cleaned_text:
                    w_copy = w.copy()
                    w_copy["text"] = cleaned_text
                    words.append(w_copy)

            if not words:
                continue

            split_points = detect_all_column_splits(words, page.width, page.height)
            num_columns = len(split_points) + 1

            if num_columns > 1:
                print(f"[Page {idx}] {num_columns} colonnes globales détectées.")
                columns_words = [[] for _ in range(num_columns)]

                for w in words:
                    word_center = (w["x0"] + w["x1"]) / 2
                    assigned = False
                    for col_idx, split_x in enumerate(split_points):
                        if word_center < split_x:
                            columns_words[col_idx].append(w)
                            assigned = True
                            break
                    if not assigned:
                        columns_words[-1].append(w)

                page_content = []
                for col_idx, col_words in enumerate(columns_words, start=1):
                    col_text = segment_words_into_blocks(col_words)
                    if col_text.strip():
                        page_content.append(f"====== COLONNE {col_idx} ======\n{col_text}")

                pages_text.append("\n\n".join(page_content))

            else:
                print(f"[Page {idx}] Layout standard (1 colonne) détecté.")
                pages_text.append(segment_words_into_blocks(words))

    return "\n\n================= NOUVELLE PAGE =================\n\n".join(pages_text)


# --- Utilisation ---
pdf_path = "C:/Users/lione/test/CV-LionelLalande-V5.4.1.pdf"
texte = extract_text_structured_layout(pdf_path)
print("\n--- RÉSULTAT DE L'EXTRACTION EN PARAGRAPHES ---\n")
print(texte)
