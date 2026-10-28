# Bundled Tesseract language models

The four `.traineddata` files under `Features/RenameByOCR/tessdata` are from
[tesseract-ocr/tessdata_best](https://github.com/tesseract-ocr/tessdata_best),
licensed under Apache-2.0 (see the adjacent LICENSE).
Their Git blob IDs were checked against upstream `main` on 2026-10-28:

| Language | Filename | Git blob SHA-1 |
| --- | --- | --- |
| English | eng.traineddata | 176dc3220de7db34d3b3aecbfa42043a6038348b |
| Czech | ces.traineddata | d762752614325b5b60733804b70b8281ab9c8543 |
| Polish | pol.traineddata | 6c149bd1f9bc6da3948ebc81be1a0d9de0a597f4 |
| Russian | rus.traineddata | 702e3c43c863cf3325002c9d2cc38045e01314a9 |

The application copies these models once to the user's local model directory,
without overwriting existing models. They are loaded with the LSTM-only engine.
User-added models may come from tessdata_best, tessdata_fast, or another
LSTM-compatible source; their respective licenses remain applicable.
