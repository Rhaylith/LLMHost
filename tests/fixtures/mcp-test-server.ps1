# Minimal MCP server over stdio used to test the MCP host (tools: add, shout).
$ErrorActionPreference = 'Stop'
function Send($obj) { [Console]::Out.WriteLine(($obj | ConvertTo-Json -Depth 20 -Compress)); [Console]::Out.Flush() }
while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { break }
    if ($line.Trim().Length -eq 0) { continue }
    $msg = $line | ConvertFrom-Json
    switch ($msg.method) {
        'initialize' {
            Send @{ jsonrpc = '2.0'; id = $msg.id; result = @{ protocolVersion = '2025-03-26'; capabilities = @{ tools = @{} }; serverInfo = @{ name = 'test-server'; version = '1.0' } } }
        }
        'tools/list' {
            $tools = @(
                @{ name = 'add'; description = 'Add two numbers and return the sum'; inputSchema = @{ type = 'object'; properties = @{ a = @{ type = 'number' }; b = @{ type = 'number' } }; required = @('a', 'b') } },
                @{ name = 'shout'; description = 'Return the text in capital letters'; inputSchema = @{ type = 'object'; properties = @{ text = @{ type = 'string' } }; required = @('text') } }
            )
            Send @{ jsonrpc = '2.0'; id = $msg.id; result = @{ tools = $tools } }
        }
        'tools/call' {
            $name = $msg.params.name; $a = $msg.params.arguments
            if ($name -eq 'add') { $text = [string]([double]$a.a + [double]$a.b) }
            elseif ($name -eq 'shout') { $text = ([string]$a.text).ToUpper() }
            else { $text = "unknown tool" }
            Send @{ jsonrpc = '2.0'; id = $msg.id; result = @{ content = @(@{ type = 'text'; text = $text }) } }
        }
        default {
            if ($null -ne $msg.id) { Send @{ jsonrpc = '2.0'; id = $msg.id; result = @{} } }
        }
    }
}
