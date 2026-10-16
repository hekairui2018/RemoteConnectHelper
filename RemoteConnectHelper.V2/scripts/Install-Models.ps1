$ErrorActionPreference = 'Stop'
$modelRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Models'))
New-Item -ItemType Directory -Path $modelRoot -Force | Out-Null
$assets = @(
    @{ Name='ch_PP-OCRv5_rec_mobile.onnx'; Url='https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx'; Sha256='5825FC7EBF84AE7A412BE049820B4D86D77620F204A041697B0494669B1742C5' },
    @{ Name='ppocrv5_dict.txt'; Url='https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt'; Sha256='D1979E9F794C464C0D2E0B70A7FE14DD978E9DC644C0E71F14158CDF8342AF1B' }
)
foreach ($asset in $assets) {
    $destination = Join-Path $modelRoot $asset.Name
    if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $asset.Sha256) {
        Write-Output "Verified $($asset.Name)"
        continue
    }
    $temporary = Join-Path $modelRoot ($asset.Name + '.' + [Guid]::NewGuid().ToString('N') + '.download')
    try {
        Invoke-WebRequest -Uri $asset.Url -OutFile $temporary
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $asset.Sha256) { throw "Model SHA256 mismatch: $($asset.Name)" }
        Move-Item -LiteralPath $temporary -Destination $destination -Force
        Write-Output "Installed $($asset.Name)"
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
