from pathlib import Path
from transformers import AutoTokenizer, AutoModelForTokenClassification
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
        try:
            model = ORTModelForTokenClassification.from_pretrained(
                self.model_id,
                export=False,
                local_files_only=False
            )
            model.save_pretrained(self.output_dir)
            return True
        except Exception:
            return False

    def export_pytorch_to_onnx(self):
        tokenizer = AutoTokenizer.from_pretrained(self.model_id)
        ORTModelForTokenClassification.from_pretrained(
            self.model_id,
            from_transformers=True,
            export=True,
            tokenizer=tokenizer,
            save_dir=self.output_dir
        )

    def run(self):
        self.download_tokenizer()

        if self.try_download_onnx():
            return "ONNX model downloaded."

        self.export_pytorch_to_onnx()
        return "PyTorch model exported to ONNX."
