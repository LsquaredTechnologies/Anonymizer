from pathlib import Path
from transformers import AutoTokenizer
from optimum.onnxruntime import ORTModelForTokenClassification


class ModelDownloader:
    def __init__(self, model_id: str, output_dir: str):
        self.model_id = model_id
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)

    def download_tokenizer(self):
        tokenizer = AutoTokenizer.from_pretrained(self.model_id)
        tokenizer.save_pretrained(self.output_dir)

    def try_download_onnx(self):
        """Tente de télécharger un modèle déjà exporté en ONNX sur le Hub."""
        try:
            model = ORTModelForTokenClassification.from_pretrained(
                self.model_id,
                export=False,
                local_files_only=False,
            )
            model.save_pretrained(self.output_dir)
            return True
        except Exception:
            return False

    def export_pytorch_to_onnx(self):
        """Exporte depuis PyTorch si aucun ONNX n'est disponible sur le Hub."""
        # from_transformers est déprécié depuis Optimum 1.14 — export=True suffit
        ORTModelForTokenClassification.from_pretrained(
            self.model_id,
            export=True,
        ).save_pretrained(self.output_dir)

    def run(self) -> str:
        self.download_tokenizer()

        if self.try_download_onnx():
            return "ONNX model downloaded."

        self.export_pytorch_to_onnx()
        return "PyTorch model exported to ONNX."
