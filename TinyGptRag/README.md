# TinyGptRag

A small GPT-style transformer **implemented entirely from scratch in C#** — including a hand-written
reverse-mode autodiff engine (`Autograd/Tensor.cs`), embeddings, causal self-attention, feed-forward
layers, an Adam optimizer, and a training loop. No pretrained weights, no ML.NET, no ONNX, no external
model API calls anywhere. Also includes a RAG (retrieval-augmented generation) layer that uses the
model's own hidden states as embeddings, and a console chat loop.

## Honest expectations

This is a **real from-scratch neural network**, but it is not a competitor to GPT/Claude/Llama. Those
are trained on trillions of tokens across huge GPU clusters. This model:

- Learns only from the text you feed it in `train`.
- Will produce fluent-ish output only on the vocabulary/style/topics present in your corpus.
- Has no general world knowledge beyond what's in your training text.
- Works best as a **narrow, domain-specific** assistant over your own documents (which is exactly what
  RAG is good for) rather than a general chatbot.

If you want broader general knowledge, you'd either need a much larger corpus + much more compute/time,
or you'd need to use a pretrained model — which was explicitly out of scope here.

## Requirements

- .NET 8 SDK (`dotnet --version` should show 8.x). Get it from https://dotnet.microsoft.com/download
- No NuGet packages are required — everything is pure C#/.NET base class library.

## Build

```bash
cd TinyGptRag
dotnet build
```

## 1. Train your own model

```bash
dotnet run -- train --corpus ./my_docs --out ./model \
  --vocab 4000 --dmodel 64 --nhead 4 --nlayer 4 --dff 256 --block 128 \
  --steps 3000 --lr 0.0003
```

- `--corpus` can be a single `.txt` file or a directory of `.txt` files (concatenated).
- Bigger `--dmodel`/`--nlayer`/`--steps` = better quality but slower training (this trains on CPU with
  plain nested-loop math — no GPU kernels — so keep sizes modest unless you're patient).
- Output: `model/model.bin` (weights) and `model/tokenizer.json` (vocabulary learned from your corpus).

## 2. Ingest documents for RAG

```bash
dotnet run -- ingest --model ./model --docs ./knowledge_base --out ./rag \
  --chunk 100 --overlap 20
```

This chunks your documents and embeds each chunk using the trained model's own hidden states
(mean-pooled), storing vectors in `rag/vectorstore.json` for cosine-similarity retrieval.

## 3. Chat

```bash
dotnet run -- chat --model ./model --rag ./rag --topk 3 --maxnew 60 --temp 0.9 --topkgen 40
```

Each turn: your message is embedded, the top-K most similar chunks are retrieved from the vector store,
and both are fed into the model as context for generation (classic retrieval-augmented generation).
You can also run `chat` without `--rag` for plain chat using only what the model learned during training.

## Project layout

```
Autograd/Tensor.cs      - from-scratch autodiff engine (matmul, softmax, layernorm, attention math, etc.)
Model/GptConfig.cs       - hyperparameters
Model/TinyGpt.cs         - the transformer itself (embeddings, causal attention blocks, save/load)
Tokenizer/WordTokenizer.cs - vocabulary built from your corpus, encode/decode
Optim/Adam.cs             - Adam optimizer from scratch
Training/Trainer.cs       - next-token-prediction training loop
Rag/VectorStore.cs        - chunking, embedding, cosine-similarity retrieval
Program.cs                - CLI: train / ingest / chat
```

## Tuning tips

- Loss should trend downward over training; if it plateaus high, try more steps, a larger corpus, or
  a larger `--dmodel`/`--nlayer`.
- If generation looks too random, lower `--temp` and/or `--topkgen`. If it's too repetitive/boring,
  raise them.
- `--block` (context window) limits both training window size and how much RAG context + question can
  fit into one prompt at chat time.
