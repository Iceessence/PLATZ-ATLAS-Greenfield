# Local ATLAS scene question experiment

## GF-021 · Grounded scene router 0.2

This offline candidate resolves a real object name, exact ID, or selection against a checksummed scene snapshot and its authorized project, scene and revision. It abstains on missing, duplicate or conflicting references. Two locally trained softmax models use word, word-pair and character features to estimate whether a request is a safe read and which read it requests. The returned intent includes the exact source ID, any linked IDs, and the evidence digest. The tool accepts synthetic fixtures and has no project-writing or network functionality.

The corpus has 392 training rows in 8 room families, 102 calibration rows in 3 families, and 218 held-out rows in 5 families. The split checks reject overlap in room families and normalized prompt text. The prompts use real fixture names, including multi-word and overlapping names, and intentionally reuse phrasing within a split. Denial cases include writes, mixed read/write requests, unauthorized sharing, prompt injection, and out-of-scope or invalid evidence. The source includes the corpus and training settings. These synthetic results measure the supplied task and leave generalization to independently authored prompts open.

The gate requires at least 90% exact supported routing and zero unsafe accepts. Calibration selected the abstain-all fallback. Stage 1 resolved **110/110** supported IDs and rejected **31/108** denial cases. The combined candidate failed at **0/110** supported and **0/108** unsafe accepts. The four-way intent head routed **100/110** correctly with its safety gate disabled and accepted **67/108** denial cases. The latter result describes the failure mode only. Studio continues to use the deterministic host.

| Method | Correct / 218 | Supported / 110 | Unsafe accepts / 108 |
| --- | ---: | ---: | ---: |
| Actual Studio read syntax | 113 | 5 | 0 |
| Keyword rules | 169 | 100 | 39 |
| Majority label | 108 | 0 | 0 |
| Unigram multinomial naive Bayes | 199 | 105 | 14 |
| Word bigram multinomial naive Bayes | 169 | 90 | 29 |
| TF-IDF cosine centroid | 184 | 110 | 34 |
| Averaged bag-of-words perceptron | 164 | 85 | 29 |
| Learned intent with safety disabled | 141 | 100 | 67 |
| Calibrated learned safety and intent | 108 | 0 | 0 |

All comparisons receive the same verified name/ID grounding. Statistical baselines use their top vote and the shared object-type eligibility check. The learned safety and intent row uses thresholds chosen from calibration only. Run the following after the offline restore command below.

```powershell
dotnet run --project tools/AtlasSpecialist -c Release --no-restore -- --grounded tools/AtlasSpecialist/grounded artifacts/grounded-specialist
Get-FileHash artifacts/grounded-specialist/model.json, artifacts/grounded-specialist/evaluation.json
```

Two runs matched byte for byte. The weights in `model.json` hash to `1079992b7e388b2371d0effec12dab68527ee69d7a2acea395886c6aa33dcfe2`. `evaluation.json` hashes to `9965e570334eccda93c4a77cbcaae50bcc2dff05b55a3d33d56a70ca6e27ee92` and contains every prediction, the calibration sweep, and hashes of all four inputs and the held-out split. The reference host keeps both complete runs under `C:\PLATZ ATLAS\.platz-atlas-verification\gf-017-021\specialist-final*`. Preserve these receipts and use a fresh reviewer-owned holdout for any next candidate.

## GF-016 · Symbolic query router 0.1

The specialist classifies a question about an already identified scene object as inspection, location, switch link, object state, or unsupported. The experiment uses a symbolic `<object>` slot. It does not model real scene names or resolve identity. Studio owns project authorization, stable object resolution, and typed edits independently.

The original multinomial naive Bayes model is fitted locally from word and adjacent-word counts. Training uses 84 synthetic examples. The held-out set contains 42 different prompts, including 18 outside the read-only profile. Both sets are committed with the training configuration. No prompts from Studio or owner projects were collected. The deterministic syntax baseline measures the documented read prefixes, the keyword baseline uses separate hand-written rules, and the frequency baseline predicts the most common training label.

The preset gates require at least 90% supported-route accuracy, zero unsupported routes accepted, and at least two more correct predictions than the strongest baseline. The runner abstains when confidence is below 0.83, the margin is below 0.16, or the query has no object slot. The first run scored 17/42 overall, routed 2/24 supported questions correctly, and accepted 3/18 unsupported questions. The strongest baseline scored 20/42. The model failed qualification. It is absent from Studio and Player. Do not tune on this same held-out set and report it as independent.

Run the experiment from the repository root with the pinned SDK. The project has zero external package dependencies.

```powershell
$root = (Get-Location).Path
$tool = Join-Path $root 'tools/AtlasSpecialist'
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
dotnet restore "$tool/AtlasSpecialist.csproj" --locked-mode --source $cache -p:NuGetAudit=false
dotnet run --project "$tool/AtlasSpecialist.csproj" -c Release --no-restore -- $tool "$root/artifacts/atlas-specialist"
```

The [reference model](reference/model.json) includes the training and configuration digests, class priors and learned feature weights. The [reference evaluation](reference/evaluation.json) includes the held-out digest, thresholds and every scored prediction. Verify a rerun with SHA-256 of the generated files. A later independent review should use previously unseen real scene names and separate read requests from write or out-of-scope requests.
