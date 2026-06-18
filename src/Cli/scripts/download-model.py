# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "numpy",
#   "optimum[onnxruntime]",
#   "transformers",
# ]
# ///
import argparse
from model_downloader import ModelDownloader


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    downloader = ModelDownloader(args.model, args.out)
    result = downloader.run()
    print(result)


if __name__ == "__main__":
    main()
