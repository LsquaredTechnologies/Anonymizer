using System.Text.RegularExpressions;

namespace Anonymizer.Extractor.PII;

/// <summary>
/// Détecteur de PII basé sur des expressions régulières déterministes.
/// </summary>
public sealed partial class RegexPiiDetector : IPiiDetector
{
    /// <summary>
    /// Analyse un texte et extrait les PII basés sur des patterns connus.
    /// </summary>
    public IReadOnlyList<Entity> AnalyzeText(string text)
    {
        List<Entity> entities = [];

        if (string.IsNullOrWhiteSpace(text)) return entities;

        // Fonction locale pour extraire et mapper les résultats vers notre modèle 'Entity'
        void Extract(Regex regex, string label, int index = 0)
        {
            foreach (Match match in regex.Matches(text))
            {
                var group = match.Groups[index];
                if(!group.Success) continue;
                entities.Add(new Entity
                {
                    Label = label,
                    Start = group.Index,
                    End = group.Index + group.Length,
                    Text = group.Value,
                    Score = 1.0f // Une Regex est déterministe, le score de confiance est donc de 100%
                });
            }
        }

        // Exécution des extractions
        //foreach (Match match in PersonFullNameRegex().Matches(text))
        //{
        //    int start = match.Index;
        //    int end = match.Index + match.Length;

        //    while (start < end && (text[start] == '#' || char.IsWhiteSpace(text[start])))
        //    {
        //        start++;
        //    }

        //    if (end <= start)
        //    {
        //        continue;
        //    }

        //    entities.Add(new Entity
        //    {
        //        Label = "NOM_PERSONNE",
        //        Start = start,
        //        End = end,
        //        Text = text[start..end],
        //        Score = 1.0f
        //    });
        //}

        Extract(EmailRegex(), "EMAIL");
        Extract(PhoneRegex(), "PHONE");
        Extract(DobRegex(), "DOB", 1);
        Extract(CityOfBirthRegex(), "CITY", 1);
        Extract(UrlRegex(), "URL");

        // Tri par position d'apparition dans le texte
        entities.Sort((a, b) => a.Start.CompareTo(b.Start));

        return entities;
    }

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?<!\d)(?:(?:\+|00)\d{1,3}[ .-]?(?:\(?\d{2,4}\)?[ .-]?){2,6}|0?\d(?:\d[ .-]?){7,12})(?!\d)\b")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\bné[e]?\s+le\s+(\d{1,2}\s+(?:[\p{L}\p{M}]{3,}|\d{1,2})\s+\d{2,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex DobRegex();

    [GeneratedRegex(@"\bné[e]?\s+le\s+\d{1,2}\s+(?:[\p{L}\p{M}]{3,}|\d{1,2})\s+\d{2,4}\s+à\s+([\p{L}\p{M}\p{N} .'\-]{2,})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CityOfBirthRegex();

    [GeneratedRegex(@"https?:\/\/(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    //// Fallback ciblé sur les en-têtes de CV: "Prénom NOM" ou "Prénom N O M" en début de ligne.
    //[GeneratedRegex(@"(?m)^(?:#+\s*)?[A-ZÀ-ÖØ-Ý][a-zà-öø-ÿ]+(?:[-'][A-ZÀ-ÖØ-Ý][a-zà-öø-ÿ]+)?[ \t\u00A0\u202F]+(?:[A-ZÀ-ÖØ-Ý]{3,}(?:[-'’][A-ZÀ-ÖØ-Ý]{2,})*|(?:[A-ZÀ-ÖØ-Ý][ \t\u00A0\u202F]+){2,}[A-ZÀ-ÖØ-Ý])\b")]
    //private static partial Regex PersonFullNameRegex();
}
