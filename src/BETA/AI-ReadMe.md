# Office AI tracing

The Office AI beta assets capture and analyze Office scenarios that load or
run Windows ML or ONNX Runtime models:

- `WPRP\OfficeAI.wprp` defines `OfficeAI-Light` for general Office performance
  investigations and `OfficeAI-ML` for AI-enabled scenarios.
- `WPAP\AI.regions.xml` creates semantic regions for ONNX Runtime session
  creation, model load, model compilation, inference, and execution-provider
  setup.
- `WPAP\AI.wpaProfile` displays those regions with Windows ML and ONNX Runtime
  events, CPU stacks, process activity, and the native GPU and NPU tables.

## Validate the recording profile

```powershell
wpr.exe -profiles .\WPRP\OfficeAI.wprp
```

## Capture an Office AI scenario

Run from an elevated PowerShell prompt in the `BETA` directory:

```powershell
wpr.exe -start .\WPRP\OfficeAI.wprp!OfficeAI-ML -filemode
# Reproduce one AI-enabled Office operation.
wpr.exe -stop .\OfficeAI-ML.etl
```

For GPU and NPU hardware attribution, add Windows' built-in
`NeuralProcessing.Light` profile:

```powershell
wpr.exe -start .\WPRP\OfficeAI.wprp!OfficeAI-ML `
    -start NeuralProcessing.Light `
    -filemode
# Reproduce one AI-enabled Office operation.
wpr.exe -stop .\OfficeAI-ML-Hardware.etl
```

The built-in profile requires a recent Windows Performance Toolkit. NPU data
also requires hardware and drivers that expose the Microsoft Compute Driver
Model (MCDM); an empty NPU table does not prove that no accelerator was used.

## Analyze the trace

Keep `WPAP\AI.regions.xml` beside `WPAP\AI.wpaProfile`, then open the trace:

```powershell
wpa.exe .\OfficeAI-ML.etl -profile .\WPAP\AI.wpaProfile
```

Use the semantic regions to identify model lifecycle and inference intervals.
Use WPA's native CPU, GPU, and NPU tables to attribute work to hardware; the
regions file intentionally does not reconstruct hardware execution intervals
from DxgKrnl packets.
