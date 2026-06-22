# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "numpy",
#   "onnxruntime",
#   "opencv-python",
#   "PyMuPDF",
#   "tokenizers"
# ]
# ///
import os
import json
import onnxruntime as ort
from tokenizers import Tokenizer
from tokenizers.pre_tokenizers import Whitespace
import numpy as np


# ---------------------------------------------------------
# Récupération des labels depuis config.json
# ---------------------------------------------------------
def load_labels_from_config(config_path: str):
    if not os.path.exists(config_path):
        raise FileNotFoundError(f"config.json introuvable : {config_path}")

    with open(config_path, "r", encoding="utf-8") as f:
        cfg = json.load(f)

    # Cas standard HuggingFace
    if "id2label" in cfg:
        # On trie par clé numérique pour garantir l'ordre correct
        id2label = cfg["id2label"]
        labels = [id2label[str(i)] for i in sorted(map(int, id2label.keys()))]
        return labels

    raise RuntimeError("Impossible de trouver 'id2label' dans config.json.")


# ---------------------------------------------------------
# Chargement dynamique du modèle à partir du répertoire parent
# ---------------------------------------------------------
def load_pii_model(models_root: str):
    pii_dir = os.path.join(models_root, "pii")

    tokenizer_path = os.path.join(pii_dir, "tokenizer.json")
    model_path = os.path.join(pii_dir, "model.onnx")
    config_path = os.path.join(pii_dir, "config.json")

    if not os.path.exists(model_path):
        raise FileNotFoundError(f"Model ONNX introuvable : {model_path}")

    if not os.path.exists(tokenizer_path):
        raise FileNotFoundError(f"Tokenizer introuvable : {tokenizer_path}")

    tokenizer = Tokenizer.from_file(tokenizer_path)
    tokenizer.pre_tokenizer = Whitespace()

    labels = load_labels_from_config(config_path)

    session = ort.InferenceSession(
        model_path,
        providers=["CPUExecutionProvider"]
    )

    return tokenizer, labels, session


# ---------------------------------------------------------
# Regroupement des entités B-XXX / I-XXX
# ---------------------------------------------------------
def group_entities(tokens, predictions):
    entities = []
    current = None

    for tok, label in zip(tokens, predictions):
        if label.startswith("B-"):
            if current:
                entities.append(current)
            current = {"type": label[2:], "text": tok}
        elif label.startswith("I-") and current:
            current["text"] += " " + tok
        else:
            if current:
                entities.append(current)
                current = None

    if current:
        entities.append(current)

    return entities


# ---------------------------------------------------------
# Extraction des PII
# ---------------------------------------------------------
def extract_pii(text: str, models_root: str):
    tokenizer, labels, session = load_pii_model(models_root)

    encoded = tokenizer.encode(text)
    input_ids = np.array([encoded.ids], dtype=np.int64)
    attention_mask = np.ones_like(input_ids, dtype=np.int64)

    ort_inputs = {
        "input_ids": input_ids,
        "attention_mask": attention_mask
    }

    outputs = session.run(None, ort_inputs)
    logits = outputs[0]

    pred_ids = logits.argmax(axis=-1)[0]
    pred_labels = [labels[i] for i in pred_ids]

    tokens = encoded.tokens
    entities = group_entities(tokens, pred_labels)

    return entities


# ---------------------------------------------------------
# Exemple d'utilisation
# ---------------------------------------------------------
if __name__ == "__main__":
    MODELS_ROOT = "./src/Cli/bin/Debug/net10.0/win-x64/models"

    text = "Lionel Lalande habite à Paris et son numéro est 06 12 34 56 78."
    pii = extract_pii(text, MODELS_ROOT)

    print("\n=== PII détectées ===")
    for ent in pii:
        print(f"- {ent['type']}: {ent['text']}")
