pub struct UnsupervisedReadingOrderDetector;

impl UnsupervisedReadingOrderDetector {
    pub fn get(&self, mut blocks: Vec<Block>) -> Vec<Block> {
        // Algorithme non supervisé basé sur l'axe X (Colonnes) en priorité, puis Y (Lignes)
        // Permet de lire complètement la colonne de gauche avant de passer à la colonne de droite
        blocks.sort_by(|a, b| {
            // Définition d'un seuil de tolérance de colonne (ex: 50 points d'écart sur l'axe X)
            let col_tolerance = 40.0;
            let x_diff = a.bbox.x - b.bbox.x;

            if x_diff.abs() > col_tolerance {
                // Colonnes distinctes -> On trie de gauche à droite
                a.bbox.x.partial_cmp(&b.bbox.x).unwrap()
            } else {
                // Même colonne -> On trie de haut en bas
                b.baseline_y.partial_cmp(&a.baseline_y).unwrap()
            }
        });

        blocks
    }
}
