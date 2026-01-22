# bankmore_e2e_curl.ps1 - E2E via Gateway (Windows PowerShell / PowerShell 7)
# Requer: curl.exe (Windows), ConvertFrom-Json

$ErrorActionPreference = "Stop"

$BaseUrl = $env:BANKMORE_GATEWAY_URL
if ([string]::IsNullOrWhiteSpace($BaseUrl)) { $BaseUrl = "http://localhost:8080"}
$BaseUrl = $BaseUrl.TrimEnd("/")

function New-GuidString { return ([guid]::NewGuid()).ToString() }

function Get-Json([string]$url, [hashtable]$headers = @{}) {
  $h = @()
  foreach ($k in $headers.Keys) { $h += @("-H", "${k}: $($headers[$k])") }

  $bodyFile = New-TemporaryFile
  $errFile  = New-TemporaryFile

  try {
    $statusStr = & curl.exe -sS -X GET $url @h -o $bodyFile -w "%{http_code}" 2> $errFile
    $body = Get-Content $bodyFile -Raw
    $err  = Get-Content $errFile -Raw

    if ($LASTEXITCODE -ne 0) {
      throw ("curl falhou (GET {0}). Detalhe: {1}" -f $url, $err.Trim())
    }

    if ([string]::IsNullOrWhiteSpace($statusStr)) { $statusStr = "000" }
    $status = [int]$statusStr

    return @{ status = $status; body = $body }
  }
  finally {
    Remove-Item $bodyFile -ErrorAction SilentlyContinue
    Remove-Item $errFile  -ErrorAction SilentlyContinue
  }
}

function Post-Json([string]$url, [string]$json, [hashtable]$headers = @{}) {
  $headers["Content-Type"] = "application/json"
  $h = @()
  foreach ($k in $headers.Keys) { $h += @("-H", "${k}: $($headers[$k])") }

  $bodyFile = New-TemporaryFile
  $errFile  = New-TemporaryFile

  try {
    $statusStr = & curl.exe -sS -X POST $url @h --data $json -o $bodyFile -w "%{http_code}" 2> $errFile
    $body = Get-Content $bodyFile -Raw
    $err  = Get-Content $errFile -Raw

    if ($LASTEXITCODE -ne 0) {
      throw ("curl falhou (POST {0}). Detalhe: {1}" -f $url, $err.Trim())
    }

    if ([string]::IsNullOrWhiteSpace($statusStr)) { $statusStr = "000" }
    $status = [int]$statusStr

    return @{ status = $status; body = $body }
  }
  finally {
    Remove-Item $bodyFile -ErrorAction SilentlyContinue
    Remove-Item $errFile  -ErrorAction SilentlyContinue
  }
}

function Ensure-Success($r, [string]$step) {
  if ($r.status -lt 200 -or $r.status -ge 300) {
    Write-Host "== FALHOU: $step ==" -ForegroundColor Red
    Write-Host ("HTTP {0}" -f $r.status) -ForegroundColor Red
    if ($r.body) { Write-Host $r.body }
    throw "Falha no passo: $step"
  }
}

# --- CPF válido (gera dígitos verificadores) ---
function New-ValidCpf {
  # gera 9 dígitos aleatórios (não usar todos iguais)
  do {
    $n = 1..9 | ForEach-Object { Get-Random -Minimum 0 -Maximum 10 }
  } while (($n | Select-Object -Unique).Count -eq 1)

  function CalcDv($arr, $factor) {
    $sum = 0
    for ($i=0; $i -lt $arr.Count; $i++) { $sum += $arr[$i] * ($factor - $i) }
    $mod = $sum % 11
    $dv = 11 - $mod
    if ($dv -ge 10) { $dv = 0 }
    return $dv
  }

  $dv1 = CalcDv $n 10
  $n2 = $n + @($dv1)
  $dv2 = CalcDv $n2 11
  $cpf = ($n + @($dv1, $dv2)) -join ""
  return $cpf
}

# ---- Dados de teste ----
$senha = "1234"
$cpfOrigem  = New-ValidCpf
$cpfDestino = New-ValidCpf

Write-Host "BaseUrl=$BaseUrl"
Write-Host "CPF Origem=$cpfOrigem | CPF Destino=$cpfDestino"

# 1) Cadastrar conta origem
Write-Host "== 1) Cadastrar conta origem =="
$r = Post-Json "$BaseUrl/conta-corrente/cadastrar" (@{ cpf=$cpfOrigem; senha=$senha } | ConvertTo-Json -Compress)
Write-Host ("HTTP {0}" -f $r.status)
if ($r.body) { Write-Host $r.body }
Ensure-Success $r "Cadastrar conta origem"
$numeroOrigem = ((($r.body | ConvertFrom-Json).numeroConta) | Out-String).Trim()
Write-Host "numeroOrigem=$numeroOrigem"

# 2) Cadastrar conta destino
Write-Host "== 2) Cadastrar conta destino =="
$r = Post-Json "$BaseUrl/conta-corrente/cadastrar" (@{ cpf=$cpfDestino; senha=$senha } | ConvertTo-Json -Compress)
Write-Host ("HTTP {0}" -f $r.status)
if ($r.body) { Write-Host $r.body }
Ensure-Success $r "Cadastrar conta destino"
$numeroDestino = ((($r.body | ConvertFrom-Json).numeroConta) | Out-String).Trim()
Write-Host "numeroDestino=$numeroDestino"

# 3) Login origem (JWT)
Write-Host "== 3) Login origem (JWT) =="
$r = Post-Json "$BaseUrl/conta-corrente/login" (@{ cpfOuNumeroConta=$cpfOrigem; senha=$senha } | ConvertTo-Json -Compress)
Write-Host ("HTTP {0}" -f $r.status)
if ($r.body) { Write-Host $r.body }
Ensure-Success $r "Login origem"
$jwt = ((($r.body | ConvertFrom-Json).token) | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($jwt)) { throw "Login não retornou token." }
Write-Host ("jwt (primeiros 20 chars)={0}..." -f $jwt.Substring(0,20))

$auth = @{ "Authorization" = "Bearer $jwt" }

# 4) Depósito 100.00 (C) - idempotente
Write-Host "== 4) Depósito 100.00 (C) =="
$idem1 = New-GuidString
$bodyMov = @{
  identificacaoRequisicao = $idem1
  numeroContaCorrente     = $numeroOrigem
  valor                   = 100.00
  tipoMovimento           = "C"
} | ConvertTo-Json -Compress
$r = Post-Json "$BaseUrl/conta-corrente/movimentar" $bodyMov ($auth + @{ "Idempotency-Key" = $idem1 })
Write-Host ("HTTP {0}" -f $r.status)
if ($r.body) { Write-Host $r.body }
Ensure-Success $r "Depósito"

# 5) Saldo origem
Write-Host "== 5) Saldo origem =="
$r = Get-Json "$BaseUrl/conta-corrente/saldo" $auth
Write-Host ("HTTP {0}" -f $r.status)
if ($r.body) { Write-Host $r.body }
Ensure-Success $r "Saldo origem"

# 6) Resolver id conta destino (internal via gateway)
Write-Host "== 6) Resolver id conta destino (internal via gateway) =="
# Gateway usa /internal/cc/* e injeta X-Internal-Api-Key automaticamente
$r = Get-Json "$BaseUrl/internal/cc/contas/$numeroDestino/id"
Write-Host ("HTTP {0}" -f $r.status)
if ($r.body) { Write-Host $r.body }
Ensure-Success $r "Resolver id conta destino"
$idContaDestino = ((($r.body | ConvertFrom-Json).idContaCorrente) | Out-String).Trim()
Write-Host "idContaDestino=$idContaDestino"

# 7) Transferir 10.00 (idempotente)
Write-Host "== 7) Transferir 10.00 =="
$idem2 = New-GuidString
$bodyTr = @{
  IdentificacaoRequisicao = $idem2
  IdContaDestino          = $idContaDestino
  Valor                   = 10.00
} | ConvertTo-Json -Compress
$r = Post-Json "$BaseUrl/transferencias/efetuar" $bodyTr ($auth + @{ "Idempotency-Key" = $idem2 })
Write-Host ("HTTP {0}" -f $r.status)
if ($r.body) { Write-Host $r.body }
Ensure-Success $r "Transferência"

# 8) Saldo origem (depois)
Write-Host "== 8) Saldo origem (depois) =="
$r = Get-Json "$BaseUrl/conta-corrente/saldo" $auth
Write-Host ("HTTP {0}" -f $r.status)
if ($r.body) { Write-Host $r.body }
Ensure-Success $r "Saldo origem depois"

# 9) Login destino + saldo destino
Write-Host "== 9) Saldo destino =="
$r = Post-Json "$BaseUrl/conta-corrente/login" (@{ cpfOuNumeroConta=$cpfDestino; senha=$senha } | ConvertTo-Json -Compress)
Write-Host ("HTTP {0}" -f $r.status)
if ($r.body) { Write-Host $r.body }
Ensure-Success $r "Login destino"
$jwt2 = ((($r.body | ConvertFrom-Json).token) | Out-String).Trim()
$auth2 = @{ "Authorization" = "Bearer $jwt2" }
$r = Get-Json "$BaseUrl/conta-corrente/saldo" $auth2
Write-Host ("HTTP {0}" -f $r.status)
if ($r.body) { Write-Host $r.body }
Ensure-Success $r "Saldo destino"

Write-Host "== OK ==" -ForegroundColor Green