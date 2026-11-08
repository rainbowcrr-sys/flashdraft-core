# Contributing / 贡献指南

## English

Welcome. This repository holds the protocol and transport core of FlashDraft.

1. Open an issue before starting a large change.
2. Use the latest stable .NET SDK; run `dotnet build FlashDraft.sln -c Release` and `dotnet test FlashDraft.sln -c Release` before opening a pull request.
3. Write all code comments in English. Documentation is bilingual. Do not use emoji.
4. Do not add PCL source code, images, logos, or fonts. Keep the PCL notice text unchanged.
5. Never commit credentials, tokens, or local configuration.

DCO is required. Every commit must carry a `Signed-off-by` trailer confirming that you have the right to submit the code under Apache-2.0:

```
git commit -s -m "Describe the change"
```

The pull request template contains the full checklist.

## 中文

欢迎贡献。本仓是 FlashDraft 的协议与传输核心。

1. 大改动请先开 issue 讨论。
2. 使用最新稳定版 .NET SDK；提交 PR 前运行 `dotnet build` 与 `dotnet test`。
3. 代码注释一律英文，文档中英双语，不使用表情符号。
4. 不得加入 PCL 的源码、图片、Logo 或字体；PCL 声明文字保持不变。
5. 禁止提交任何凭据、令牌或本地配置。

必须遵守 DCO：每笔 commit 需带 `Signed-off-by`，确认你有权以 Apache-2.0 提交该代码。PR 模板内含完整检查清单。
