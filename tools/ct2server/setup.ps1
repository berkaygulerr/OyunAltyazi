# OyunAltyazi yerel çeviri kurulumu: venv + ctranslate2 + opus-mt-tc-big-en-tr modeli (CC BY 4.0)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'OyunAltyazi'
$venv = Join-Path $root 'ct2\venv'
$model = Join-Path (Join-Path $env:LOCALAPPDATA 'OyunAltyaziData') 'models\opus-mt-tc-big-en-tr-ct2'
$tmpv = Join-Path $root 'ct2\convvenv'
New-Item -ItemType Directory -Force -Path $root | Out-Null
if (-not (Test-Path "$venv\Scripts\python.exe")) { py -3.12 -m venv $venv }
& "$venv\Scripts\python.exe" -m pip install -q --upgrade pip
& "$venv\Scripts\python.exe" -m pip install -q ctranslate2 sentencepiece huggingface_hub nvidia-cublas-cu12 nvidia-cudnn-cu12==9.*
if (-not (Test-Path "$model\model.bin")) {
  py -3.12 -m venv $tmpv
  $p = "$tmpv\Scripts\python.exe"
  & $p -m pip install -q torch --index-url https://download.pytorch.org/whl/cpu
  & $p -m pip install -q ctranslate2 transformers sentencepiece huggingface_hub
  New-Item -ItemType Directory -Force -Path (Split-Path $model) | Out-Null
  & "$tmpv\Scripts\ct2-transformers-converter.exe" --model Helsinki-NLP/opus-mt-tc-big-en-tr --output_dir $model --quantization int8_float16 --force
  & $p -c "from huggingface_hub import hf_hub_download as d; import shutil,sys; [shutil.copy(d('Helsinki-NLP/opus-mt-tc-big-en-tr',f), sys.argv[1]) for f in ('source.spm','target.spm')]" $model
  Remove-Item -Recurse -Force $tmpv
}
Write-Host "Kurulum tamam: $model"
