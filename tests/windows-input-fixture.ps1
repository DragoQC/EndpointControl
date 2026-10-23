# Run in the Windows test user's interactive console session.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$directory = Join-Path $env:TEMP ('MeshAll-Input-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$output = Join-Path $directory 'result.txt'
Write-Output "Verification output: $output"

$form = New-Object Windows.Forms.Form
$form.Text = 'MeshAll Input Verification'
$form.StartPosition = 'Manual'
$form.Location = New-Object Drawing.Point(100, 100)
$form.ClientSize = New-Object Drawing.Size(600, 180)
$form.TopMost = $true
$text = New-Object Windows.Forms.TextBox
$text.SetBounds(20, 25, 560, 30)
$button = New-Object Windows.Forms.Button
$button.Text = 'Save verification and close'
$button.SetBounds(20, 80, 230, 35)
$button.Add_Click({
    [IO.File]::WriteAllText($output, $text.Text)
    $form.Close()
})
$form.Controls.Add($text)
$form.Controls.Add($button)
$form.Add_Shown({ $text.Focus() })
try { [void]$form.ShowDialog() }
finally { $form.Dispose() }
