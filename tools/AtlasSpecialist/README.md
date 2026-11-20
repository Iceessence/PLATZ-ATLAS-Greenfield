# Local ATLAS scene question experiment

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
