param([string]$Root, [string]$Out)
$sizes = 16,24,32,48,64,128,256
$imgs = foreach ($s in $sizes) {
  $p = Join-Path $Root "ico-$s.png"
  if (-not (Test-Path $p)) { throw "missing $p" }
  [pscustomobject]@{ Size = $s; Bytes = [IO.File]::ReadAllBytes($p) }
}
$fs = [IO.File]::Create($Out)
$w  = New-Object IO.BinaryWriter($fs)
# ICONDIR
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$imgs.Count)
# entries are fixed-width, so the first image starts after all of them
$offset = 6 + 16 * $imgs.Count
foreach ($i in $imgs) {
  $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }   # 0 means 256 in an ICO
  $w.Write([byte]$dim); $w.Write([byte]$dim)
  $w.Write([byte]0);    $w.Write([byte]0)              # palette count, reserved
  $w.Write([uint16]1);  $w.Write([uint16]32)           # planes, bits per pixel
  $w.Write([uint32]$i.Bytes.Length)
  $w.Write([uint32]$offset)
  $offset += $i.Bytes.Length
}
foreach ($i in $imgs) { $w.Write($i.Bytes) }
$w.Flush(); $w.Close(); $fs.Close()
"wrote $Out ($((Get-Item $Out).Length) bytes, $($imgs.Count) sizes)"
