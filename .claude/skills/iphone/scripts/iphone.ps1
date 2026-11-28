# iphone.ps1 - the user's iPhone from a computer in the same Wi-Fi, through LiteBro for iOS
# ("Access from PC"): files, pictures of the screen, taps and text, builds.
# Windows PowerShell 5.1 and later, nothing to install. The same commands and output as iphone.py beside it.
#
# For whoever changes this file:
# - Keep it ASCII: Windows PowerShell reads a script without a mark as the system's old code page.
# - Only what PowerShell 5.1 has (no ternary, no ??, no &&), and no closures: actions are lists of steps.
# - A function gives back only what its last "return" names: everything else that yields a value is sent to $null.
# - Numbers are written and read with the invariant culture: a Russian Windows has a decimal comma.
# - Names of variables do not tell capitals from small letters, and a function sees the variables of whoever
#   called it. So what is set at the top of the script has names no function uses for its own ($StateFile, not $STATE).

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Inv = [System.Globalization.CultureInfo]::InvariantCulture
[System.Threading.Thread]::CurrentThread.CurrentCulture = $Inv
$Utf8 = New-Object System.Text.UTF8Encoding($false)
try { [Console]::OutputEncoding = $Utf8 } catch { }
# Lines go out as UTF-8 whatever the console's code page is: the tools that run this read them so
$StdOut = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $Utf8)
$StdOut.AutoFlush = $true
$StdErr = New-Object System.IO.StreamWriter([Console]::OpenStandardError(), $Utf8)
$StdErr.AutoFlush = $true
[System.Net.ServicePointManager]::Expect100Continue = $false
[System.Net.ServicePointManager]::UseNagleAlgorithm = $false
try { [System.Net.WebRequest]::DefaultWebProxy = $null } catch { }   # the phone is in the home network: no proxy, and no search for one

$HelpText = @'
iphone - the user's iPhone through LiteBro for iOS. Addresses come from the phone: LiteBro,
menu "...", "Access from PC" (files: port 8080 or 8082-8089, screen: port 8081).

  connect <url> [<url>]        remember the files and/or screen address and check them
                               (or --files <url> --screen <url> to say which is which)
  status                       what answers now: files, taps, screen, the app's build
  wait [files|screen] [--seconds 120]      until the address answers
  forget                       drop the remembered addresses

  ls [path] [--json]           a folder of the phone (paths start at the shared root)
  tree [path] [--depth 2] [--max 1000]
  find <path> <pattern> [--depth 6] [--max 200]     names by a wildcard, e.g. "*.mov"
  du [path]                    sizes of what is inside
  cat <path> [--tail N] [--head N]        a text file
  plist <path>                 a property list (binary too) as JSON
  apps [filter] [--groups]     data folders of the apps with their bundle ids
  get <remote> [local] [-r]    download a file (a folder with -r)
  put <local> <remote> [-r]    upload
  mkdir <remote>   rm <remote> [-r]   mv <from> <to> [--force]   cp <from> <to> [--force]

  shot [file] [--wait 12]      a picture of the phone's screen (needs the broadcast)
  shot [file] --app            a picture of LiteBro's own screen, made by the app: no broadcast needed
                               (shot alone gives it too when the screen address is not set or does not answer)
  watch [dir] [--seconds 10] [--until-change]       pictures as the screen changes

  page [--json]                what LiteBro shows: the address, the title, whether it is loading
  open <address> [--new-tab] [--wait 20]   open an address in LiteBro on the phone
  js <code> | --file <file> [--timeout 20] JavaScript in the page: the body of an async function, its return comes back
  click <x> <y>                press a button or a switch of LiteBro itself at a point (not what a page draws)
  remote <name> [json | key=value ...]     any other test command of the phone, as it is

  tap <x> <y>                  a tap inside LiteBro; x y are 0..1 or pixels of the last shot
  hold <x> <y> [--ms 800]      a long press
  swipe <x1> <y1> <x2> <y2> [--ms 300] [--steps 12]
  type <text> [--enter]        text into the focused field of LiteBro (--file <file>: any text)
  key backspace|enter [--count N]
  button home|side --yes       a button of the phone (takes LiteBro off the screen)
  touch [json | key=value ...] [--via app|screen]    the raw request; alone: what the phone can do
    for tap, hold, swipe, type, key, click, button: --shot [--out file] looks at the screen after the action
    (without the screen address, or with --app: at LiteBro's own screen; a button needs the screen address);
    for tap, hold, swipe: --system sends a system-wide finger, --opt key=value passes an option of the
    touch request (recipe, masks, sender, ...), --of WxH says what the pixels are of

  log [--tail 60] [--launch]   LiteBro's own log on the phone
  install                      TrollStore installs LiteBro's released build (the "ios" release)
  install <file.tipa>          put another build into LiteBro's folder on the phone, to open with TrollStore by hand

Exit codes: 0 done, 1 wrong use, 2 the phone does not answer, 3 the phone refused (403),
4 not found (404), 5 no picture.
'@

$StateFile = $env:LITEBRO_IPHONE_STATE
if (-not $StateFile) { $StateFile = [System.IO.Path]::Combine([Environment]::GetFolderPath('UserProfile'), '.litebro-iphone.json') }
$AnswerWait = 8.0   # seconds to wait for an answer
if ($env:LITEBRO_IPHONE_TIMEOUT) {
    $parsed = 0.0
    if ([double]::TryParse($env:LITEBRO_IPHONE_TIMEOUT, [System.Globalization.NumberStyles]::Float, $Inv, [ref]$parsed) -and $parsed -gt 0) { $AnswerWait = $parsed }
}
$RefusedWords = 'refused (nothing listens there: access is closed, its time is over, or the broadcast is not running)'
$SilentWords = 'timeout (LiteBro is not on the screen, or the phone is asleep, in another network, or the address is wrong)'
# The one address LiteBro on the phone takes for an install asked from a computer (FileShare.ownBuild)
$OwnBuild = 'https://github.com/ronanime-arch/LiteBro/releases/download/ios/LiteBro-PC-install.tipa'
$LogPaths = @('Media/LiteBro-Debug/log.txt', 'Debug/log.txt')
$KeptWait = 0.4      # the kept picture is sent at once; what comes later than this is live
$PaceWait = 0.15     # the phone encodes a picture at most every 0.12 s: changes closer than that are not all seen
$NeverNumber = 2147483647   # no picture has this number: asking after it brings the number alone
$PollWait = 0.12     # how often the screen is asked while it is followed
$OwnSettle = 0.8     # s after an action before LiteBro pictures its own screen
$NoOwn = 'this build of LiteBro cannot picture its own screen: update it on the phone (iphone install), or use the screen broadcast (iphone connect <the screen address from the phone>)'
$Clock = [System.Diagnostics.Stopwatch]::StartNew()
$AnsweredFrom = @{}   # host:port that answered in this run: a connection to it may be one kept from before

function Say([string]$text) { $StdOut.WriteLine($text) }
function Note([string]$text) { $StdErr.WriteLine($text) }
function Now { return $Clock.Elapsed.TotalSeconds }
function Wait-Seconds([double]$seconds) { if ($seconds -gt 0) { [System.Threading.Thread]::Sleep([int][Math]::Ceiling($seconds * 1000)) } }

# A failure with the exit code it means
function Fail([int]$code, [string]$text) {
    $e = New-Object System.Exception $text
    $e.Data['code'] = $code
    throw $e
}

# The same for an address where nothing listens, while the phone itself is there
function Fail-Refused([string]$text) {
    $e = New-Object System.Exception $text
    $e.Data['code'] = 2
    $e.Data['refused'] = $true
    throw $e
}

function Is-Refused($err) {
    $e = $err.Exception
    while ($e) {
        if ($e.Data.Contains('refused')) { return $true }
        $e = $e.InnerException
    }
    return $false
}

# The exit code a caught error carries (0: not one of ours), and its text
function FailCode($err) {
    $e = $err.Exception
    while ($e) {
        if ($e.Data.Contains('code')) { return [int]$e.Data['code'] }
        $e = $e.InnerException
    }
    return 0
}

function FailText($err) {
    $e = $err.Exception
    while ($e) {
        if ($e.Data.Contains('code')) { return $e.Message }
        $e = $e.InnerException
    }
    $e = $err.Exception
    while ($e.InnerException) { $e = $e.InnerException }
    return $e.Message
}

# Numbers as iphone.py writes them: %.4f and %.1f (a half goes to the even digit), %g
function F4([double]$v) { return ([Math]::Round([decimal]$v, 4, [System.MidpointRounding]::ToEven)).ToString('F4', $Inv) }
function F1([double]$v) { return ([Math]::Round([decimal]$v, 1, [System.MidpointRounding]::ToEven)).ToString('F1', $Inv) }
function G([double]$v) { return $v.ToString($Inv) }

# --- arguments ---

# Positional arguments, --flags without a value, --options with one, --many that repeat.
# Gives @{ Pos = <list>; Opt = <hashtable> }.
function Parse-Args($argv, $flags, $options, $many) {
    $argv = @($argv)
    $pos = New-Object System.Collections.Generic.List[string]
    $opt = @{}
    $i = 0
    while ($i -lt $argv.Count) {
        $a = [string]$argv[$i]
        $name = $null
        if ($a.StartsWith('--')) { $name = $a.Substring(2) } elseif ($a -ceq '-r') { $name = 'recursive' }
        if ($null -eq $name -or $a -ceq '--') {
            $pos.Add($a)
        } elseif ($flags -ccontains $name) {
            $opt[$name] = $true
        } elseif (($options -ccontains $name) -or ($many -ccontains $name)) {
            if ($i + 1 -ge $argv.Count) { Fail 1 ('--' + $name + ' needs a value') }
            $i++
            if ($many -ccontains $name) {
                if (-not $opt.ContainsKey($name)) { $opt[$name] = New-Object System.Collections.Generic.List[string] }
                $opt[$name].Add([string]$argv[$i])
            } else {
                $opt[$name] = [string]$argv[$i]
            }
        } else {
            Fail 1 ('unknown option ' + $a + ' (see: iphone help)')
        }
        $i++
    }
    return @{ Pos = $pos; Opt = $opt }
}

function Opt-Number($opt, [string]$name, [double]$default) {
    if (-not $opt.ContainsKey($name)) { return $default }
    $v = 0.0
    if (-not [double]::TryParse([string]$opt[$name], [System.Globalization.NumberStyles]::Float, $Inv, [ref]$v)) { Fail 1 ('--' + $name + ' takes a number') }
    return $v
}

function Opt-Int($opt, [string]$name, [int]$default) {
    if (-not $opt.ContainsKey($name)) { return $default }
    $v = 0
    if (-not [int]::TryParse([string]$opt[$name], [System.Globalization.NumberStyles]::Integer, $Inv, [ref]$v)) { Fail 1 ('--' + $name + ' takes a number') }
    return $v
}

# --- JSON ---

# A piece of JSON text to be sent as it is
function Raw([string]$json) { return New-Object PSObject -Property @{ __raw = $json } }

function Json-String([string]$s) {
    if ($s -cmatch '\A[ !#-\[\]-~]*\z') { return '"' + $s + '"' }   # nothing to escape: most texts, and at once
    $b = New-Object System.Text.StringBuilder
    [void]$b.Append('"')
    foreach ($c in $s.ToCharArray()) {
        $n = [int]$c
        if ($c -eq '"') { [void]$b.Append('\"') }
        elseif ($c -eq '\') { [void]$b.Append('\\') }
        elseif ($n -eq 10) { [void]$b.Append('\n') }
        elseif ($n -eq 13) { [void]$b.Append('\r') }
        elseif ($n -eq 9) { [void]$b.Append('\t') }
        elseif ($n -eq 8) { [void]$b.Append('\b') }
        elseif ($n -eq 12) { [void]$b.Append('\f') }
        elseif ($n -lt 32 -or $n -gt 126) { [void]$b.Append('\u' + $n.ToString('x4', $Inv)) }
        else { [void]$b.Append($c) }
    }
    [void]$b.Append('"')
    return $b.ToString()
}

# A number with a fraction as iphone.py writes it: 1.0, 0.25, 1e-05
function Json-Double([double]$v) {
    if ([double]::IsNaN($v)) { return 'NaN' }
    if ([double]::IsPositiveInfinity($v)) { return 'Infinity' }
    if ([double]::IsNegativeInfinity($v)) { return '-Infinity' }
    $t = $v.ToString('R', $Inv)
    if ($t.Contains('E')) { return $t.Replace('E', 'e') }
    if (-not $t.Contains('.')) { return $t + '.0' }
    return $t
}

# Any value as JSON text: only ASCII comes out, so no code page can spoil it
function To-Json($v) {
    if ($null -eq $v) { return 'null' }
    if ($v -is [bool]) { if ($v) { return 'true' } else { return 'false' } }
    if ($v -is [string]) { return Json-String $v }
    if ($v -is [char]) { return Json-String ([string]$v) }
    if ($v -is [double] -or $v -is [single]) { return Json-Double ([double]$v) }
    if ($v -is [decimal]) { return $v.ToString($Inv) }
    if ($v -is [int] -or $v -is [long] -or $v -is [byte] -or $v -is [int16] -or $v -is [uint16] -or $v -is [uint32] -or $v -is [uint64] -or $v -is [sbyte]) { return $v.ToString($Inv) }
    if ($v -is [System.Collections.IDictionary]) {
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($k in @($v.get_Keys())) { $parts.Add((Json-String ([string]$k)) + ': ' + (To-Json $v[[string]$k])) }
        return '{' + ($parts -join ', ') + '}'
    }
    if ($v -is [System.Management.Automation.PSCustomObject]) {
        $raw = $v.PSObject.Properties['__raw']
        if ($raw) { return [string]$raw.Value }
        return Json-String ([string]$v)
    }
    if ($v -is [System.Collections.IEnumerable]) {
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($x in $v) { $parts.Add((To-Json $x)) }
        return '[' + ($parts -join ', ') + ']'
    }
    return Json-String ([string]$v)
}

# The same on lines with sorted keys: json.dumps(v, indent=1, sort_keys=True) of iphone.py
function To-Json-Lines($v, [int]$level) {
    $pad = ' ' * ($level + 1)
    if ($v -is [System.Collections.IDictionary]) {
        $keys = New-Object System.Collections.Generic.List[string]
        foreach ($k in @($v.get_Keys())) { $keys.Add([string]$k) }
        if ($keys.Count -eq 0) { return '{}' }
        $keys.Sort([System.StringComparer]::Ordinal)
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($k in $keys) { $parts.Add($pad + (Json-String $k) + ': ' + (To-Json-Lines $v[[string]$k] ($level + 1))) }
        return "{`n" + ($parts -join ",`n") + "`n" + (' ' * $level) + '}'
    }
    if ($v -is [System.Collections.IList]) {
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($x in $v) { $parts.Add($pad + (To-Json-Lines $x ($level + 1))) }
        if ($parts.Count -eq 0) { return '[]' }
        return "[`n" + ($parts -join ",`n") + "`n" + (' ' * $level) + ']'
    }
    return To-Json $v
}

# What a reader of JSON gave, as ordered dictionaries, arrays and plain values
function As-Plain($v) {
    if ($null -eq $v) { return $null }
    if ($v -is [string]) { return $v }
    if ($v -is [System.Collections.IDictionary]) {
        $o = New-Object System.Collections.Specialized.OrderedDictionary
        foreach ($pair in $v.GetEnumerator()) { $o[[string]$pair.Key] = As-Plain $pair.Value }
        return $o
    }
    if ($v -is [System.Collections.IList]) {
        $list = New-Object System.Collections.Generic.List[object]
        foreach ($x in $v) { $list.Add((As-Plain $x)) }
        return ,$list.ToArray()
    }
    if ($v -is [datetime]) { return $v.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $Inv) }
    return $v
}

# The same from System.Text.Json (PowerShell 7)
function Json-Element($el) {
    $kind = [string]$el.ValueKind
    if ($kind -eq 'Object') {
        $o = New-Object System.Collections.Specialized.OrderedDictionary
        foreach ($p in $el.EnumerateObject()) { $o[[string]$p.Name] = Json-Element $p.Value }
        return $o
    }
    if ($kind -eq 'Array') {
        $list = New-Object System.Collections.Generic.List[object]
        foreach ($x in $el.EnumerateArray()) { $list.Add((Json-Element $x)) }
        return ,$list.ToArray()
    }
    if ($kind -eq 'String') { return $el.GetString() }
    if ($kind -eq 'Number') {
        $n = [long]0
        if ($el.TryGetInt64([ref]$n)) { return $n }
        return $el.GetDouble()
    }
    if ($kind -eq 'True') { return $true }
    if ($kind -eq 'False') { return $false }
    return $null
}

# JSON text as ordered dictionaries, arrays and plain values. Gives @{ Ok; Value }: Ok is $false when it is not JSON.
# Not ConvertFrom-Json: it stops at 2 MB in Windows PowerShell, and in PowerShell 7 turns text that looks like
# a date into a date and takes a list apart.
function From-Json([string]$text) {
    if ($null -eq $text -or $text.Trim().Length -eq 0) { return @{ Ok = $false; Value = $null } }
    try {
        if ($PSVersionTable.PSVersion.Major -ge 6) {
            $doc = [System.Text.Json.JsonDocument]::Parse($text)
            try { return @{ Ok = $true; Value = (Json-Element $doc.RootElement) } } finally { $doc.Dispose() }
        }
        if ($null -eq $script:JsonReader) {
            Add-Type -AssemblyName System.Web.Extensions
            $script:JsonReader = New-Object System.Web.Script.Serialization.JavaScriptSerializer
            $script:JsonReader.MaxJsonLength = [int]::MaxValue
        }
        return @{ Ok = $true; Value = (As-Plain ($script:JsonReader.DeserializeObject($text))) }
    } catch {
        return @{ Ok = $false; Value = $null }
    }
}

function Is-Object($v) { return ($v -is [System.Collections.IDictionary]) }

# A field of an object that came from JSON: $null when it is not there
function Field($obj, [string]$name) {
    if (($obj -is [System.Collections.IDictionary]) -and $obj.Contains($name)) { return ,$obj[$name] }   # the comma: a list stays a list
    return $null
}

function Has-Field($obj, [string]$name) {
    return (($obj -is [System.Collections.IDictionary]) -and $obj.Contains($name))
}

# The value of key=value as JSON text: true, false, null and numbers as such, quoted JSON as it is, the rest as text
function Json-Literal([string]$value) {
    if ($value -cmatch '^(true|false|null)$') { return $value }
    if ($value -cmatch '^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$') { return $value }
    if ($value.Length -gt 0 -and '"{['.IndexOf($value[0]) -ge 0) {
        $got = From-Json $value
        if ($got.Ok) { return $value }
    }
    return Json-String $value
}

# key=value items added to an ordered request
function Add-Pairs($into, $items) {
    foreach ($pair in $items) {
        $at = $pair.IndexOf('=')
        if ($at -le 0) { Fail 1 ('not key=value: ' + $pair) }
        $into[$pair.Substring(0, $at)] = Raw (Json-Literal ($pair.Substring($at + 1)))
    }
}

# The number a request value is, or $null (a value given as key=value is JSON text)
function Number-Of($v) {
    if ($null -eq $v -or $v -is [bool]) { return $null }
    if ($v -is [double] -or $v -is [single] -or $v -is [decimal] -or $v -is [int] -or $v -is [long]) { return [double]$v }
    if ($v -is [System.Management.Automation.PSCustomObject]) {
        $raw = $v.PSObject.Properties['__raw']
        if ($raw) {
            $n = 0.0
            if ([double]::TryParse([string]$raw.Value, [System.Globalization.NumberStyles]::Float, $Inv, [ref]$n)) { return $n }
        }
    }
    return $null
}

# --- the remembered addresses ---

# @{ files; screen; shot = @{ w; h } }, only what is there
function Load-State {
    $state = @{}
    try {
        if (-not [System.IO.File]::Exists($StateFile)) { return $state }
        $got = From-Json ([System.IO.File]::ReadAllText($StateFile, $Utf8))
        if (-not $got.Ok) { return $state }
        foreach ($k in @('files', 'screen')) {
            $v = Field $got.Value $k
            if ($v -is [string] -and $v) { $state[$k] = $v }
        }
        $shot = Field $got.Value 'shot'
        if (Is-Object $shot) {
            $w = Number-Of (Field $shot 'w')
            $h = Number-Of (Field $shot 'h')
            if ($null -ne $w -and $null -ne $h) { $state['shot'] = @{ w = [int]$w; h = [int]$h } }
        }
    } catch { }
    return $state
}

function Save-State($state) {
    $o = New-Object System.Collections.Specialized.OrderedDictionary
    foreach ($k in @('files', 'screen')) { if ($state.ContainsKey($k)) { $o[$k] = $state[$k] } }
    if ($state.ContainsKey('shot')) {
        $shot = New-Object System.Collections.Specialized.OrderedDictionary
        $shot['w'] = [int]$state['shot'].w
        $shot['h'] = [int]$state['shot'].h
        $o['shot'] = $shot
    }
    [System.IO.File]::WriteAllText($StateFile, (To-Json $o), $Utf8)   # the address is the password: it stays in the user's own folder
}

# --- the two addresses of the phone ---

# One of them: http://host:port/<secret>/ as @{ Host; Port; Base; Url; Timeout }
function New-Server([string]$url, [double]$timeout) {
    $u = $null
    $clean = $url.Trim()
    if (-not [System.Uri]::TryCreate($clean, [System.UriKind]::Absolute, [ref]$u)) { $u = $null }
    $first = ''
    if ($u) {
        foreach ($p in $u.AbsolutePath.Split('/')) { if ($p) { $first = $p; break } }
    }
    if (-not $u -or $u.Scheme -cne 'http' -or -not $u.Host -or -not $first) {
        Fail 1 ('not an address of the phone: ' + $url + ' (it looks like http://192.168.1.23:8080/<secret>/)')
    }
    $t = $AnswerWait
    if ($timeout -gt 0) { $t = [Math]::Min($timeout, $AnswerWait) }
    $base = '/' + $first + '/'   # the secret; a pasted .../stream still works
    return @{ Host = $u.Host; Port = $u.Port; Base = $base; Url = ('http://' + $u.Host + ':' + $u.Port + $base); Timeout = $t }
}

# A path of the phone as the address carries it: every part percent-encoded
function Server-Path($server, [string]$rel) {
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($p in $rel.Replace('\', '/').Split('/')) { if ($p) { $parts.Add([System.Uri]::EscapeDataString($p)) } }
    return $server.Base + ($parts -join '/')
}

function Need-Server([string]$kind) {
    $state = Load-State
    if (-not $state.ContainsKey($kind)) { Fail 1 ('no ' + $kind + ' address yet: iphone connect <address from the phone>') }
    return New-Server $state[$kind] 0
}

function Check-Status([int]$status, [string]$what, $ok) {
    if ($null -eq $ok) { $ok = @(200, 201, 204, 206, 207) }
    if ($ok -contains $status) { return }
    if ($status -eq 403) { Fail 3 ($what + ': the phone refused (403)') }
    if ($status -eq 404) { Fail 4 ($what + ': not found (404)') }
    Fail 1 ($what + ': the phone answered ' + $status)
}

# Why nothing came back, as the exit code 2 with the words for it
function Fail-NoAnswer($server, $webError) {
    $where = 'no answer from ' + $server.Host + ':' + $server.Port + ': '
    $socket = $null
    $e = $webError
    while ($e) {
        if ($e -is [System.Net.Sockets.SocketException]) { $socket = $e; break }
        $e = $e.InnerException
    }
    $status = [string]$webError.Status
    if ($socket -and $socket.SocketErrorCode -eq [System.Net.Sockets.SocketError]::ConnectionRefused) { Fail-Refused ($where + $RefusedWords) }
    if ($status -eq 'Timeout' -or ($socket -and $socket.SocketErrorCode -eq [System.Net.Sockets.SocketError]::TimedOut)) { Fail 2 ($where + $SilentWords) }
    if ($status -eq 'ConnectFailure' -and -not $socket) { Fail-Refused ($where + $RefusedWords) }
    Fail 2 ($where + $webError.Message)
}

function Web-Error($err) {
    $e = $err.Exception
    while ($e) {
        if ($e -is [System.Net.WebException]) { return $e }
        $e = $e.InnerException
    }
    return $null
}

# One request. $body: bytes, or @{ File = <path> } to send a file as it is; $save: a path to write the answer of a
# 200 to. Gives @{ Status; Headers; Data } (Data: the bytes, or the number of bytes saved).
function Send-Request($server, [string]$method, [string]$path, $body, $headers, [double]$timeout, [string]$save) {
    $seconds = $server.Timeout
    if ($timeout -gt 0) { $seconds = $timeout }
    $address = $server.Host + ':' + $server.Port
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $response = $null
        # A connection kept from before that the phone has closed meanwhile is made again, once. A first
        # connection that fails is not: the phone may have done what was asked.
        $again = ($attempt -eq 0) -and $AnsweredFrom.ContainsKey($address)
        try {
            $request = [System.Net.HttpWebRequest][System.Net.WebRequest]::Create('http://' + $server.Host + ':' + $server.Port + $path)
            $request.Method = $method
            $request.Proxy = $null                 # never through a system proxy to the home network
            $request.AllowAutoRedirect = $false
            $request.KeepAlive = $true
            $request.Timeout = [int]($seconds * 1000)
            $request.ReadWriteTimeout = [int]($seconds * 1000)
            if ($headers) {
                foreach ($k in @($headers.get_Keys())) {
                    if ($k -eq 'Content-Type') { $request.ContentType = [string]$headers[$k] }
                    elseif ($k -eq 'Range-Last') { $request.AddRange([long](-[long]$headers[$k])) }
                    elseif ($k -eq 'Range-First') { $request.AddRange([long]0, [long]$headers[$k]) }
                    else { $request.Headers.Add([string]$k, [string]$headers[$k]) }
                }
            }
            if ($body -is [byte[]]) {
                $request.ContentLength = $body.Length
                $up = $request.GetRequestStream()
                try { $up.Write($body, 0, $body.Length) } finally { $up.Close() }
            } elseif ($body -is [System.Collections.IDictionary]) {
                $file = [System.IO.File]::OpenRead([string]$body.File)
                try {
                    $request.AllowWriteStreamBuffering = $false
                    $request.ContentLength = $file.Length
                    $up = $request.GetRequestStream()
                    try {
                        $piece = New-Object byte[] 262144
                        while ($true) {
                            $n = $file.Read($piece, 0, $piece.Length)
                            if ($n -le 0) { break }
                            $up.Write($piece, 0, $n)
                        }
                    } finally { $up.Close() }
                } finally { $file.Close() }
            }
            try {
                $response = [System.Net.HttpWebResponse]$request.GetResponse()
            } catch {
                $web = Web-Error $_
                if (-not $web -or -not $web.Response) { throw }
                $response = [System.Net.HttpWebResponse]$web.Response   # 4xx and 5xx come as errors with the answer inside
            }
            $status = [int]$response.StatusCode
            $answerHeaders = $response.Headers
            $AnsweredFrom[$address] = $true
            $came = [long]0
            $data = $null
            try {
                $in = $response.GetResponseStream()
                try {
                    if ($save -and $status -eq 200) {
                        $target = [System.IO.File]::Create($save)
                        try {
                            $piece = New-Object byte[] 262144
                            while ($true) {
                                $n = $in.Read($piece, 0, $piece.Length)
                                if ($n -le 0) { break }
                                $target.Write($piece, 0, $n)
                                $came += $n
                            }
                        } finally { $target.Close() }
                        $data = $came
                    } else {
                        $memory = New-Object System.IO.MemoryStream
                        $in.CopyTo($memory)
                        $data = $memory.ToArray()
                    }
                } finally { $in.Close() }
            } catch {
                # The answer began and did not end: not asked again, the caller says what broke off
                $inner = $_.Exception
                while ($inner.InnerException) { $inner = $inner.InnerException }
                $broke = New-Object System.Exception ('the answer from ' + $address + ' broke off: ' + $inner.Message)
                $broke.Data['code'] = 2
                $broke.Data['broke'] = [string]$inner.Message
                $broke.Data['after'] = $came
                throw $broke
            }
            return @{ Status = $status; Headers = $answerHeaders; Data = $data }
        } catch {
            if (FailCode $_) { throw }
            $web = Web-Error $_
            if (-not $web) {
                $inner = $_.Exception
                while ($inner.InnerException) { $inner = $inner.InnerException }
                if ($again -and $inner -is [System.IO.IOException]) { continue }
                Fail 2 ('no answer from ' + $address + ': ' + $inner.Message)
            }
            $kind = [string]$web.Status
            if ($again -and ($kind -eq 'KeepAliveFailure' -or $kind -eq 'ConnectionClosed' -or $kind -eq 'ReceiveFailure' -or $kind -eq 'SendFailure')) { continue }
            Fail-NoAnswer $server $web
        } finally {
            if ($response) { $response.Close() }
        }
    }
}

function Text-Of-Bytes($data) {
    if ($null -eq $data -or $data.Length -eq 0) { return '' }
    return $Utf8.GetString($data)
}

# A name from the phone in the form people type: iOS stores names decomposed, and finds them in either form
function Nfc([string]$name) { return $name.Normalize([System.Text.NormalizationForm]::FormC) }

function Local-Path([string]$p) { return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($p) }

function PJoin([string]$a, [string]$b) { return ($a.Trim('/') + '/' + $b).Trim('/') }

function Split-Lines([string]$text) {
    $lines = New-Object System.Collections.Generic.List[string]
    if (-not $text) { return ,$lines }
    foreach ($l in [regex]::Split($text, "\r\n|[\n\r\x0b\x0c\x1c\x1d\x1e\x85\u2028\u2029]")) { $lines.Add($l) }   # as str.splitlines of Python
    if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') { $lines.RemoveAt($lines.Count - 1) }
    return ,$lines
}

# --- files ---

function Xml-Child($node, [string]$name) {
    if ($null -eq $node) { return $null }
    foreach ($c in $node.ChildNodes) { if ($c.LocalName -ceq $name) { return $c } }
    return $null
}

function Compare-Entries($a, $b) {
    if ($a.dir -ne $b.dir) { if ($a.dir) { return -1 } else { return 1 } }
    $c = [string]::CompareOrdinal($a.name.ToLowerInvariant(), $b.name.ToLowerInvariant())
    if ($c -ne 0) { return $c }
    return [string]::CompareOrdinal($a.name, $b.name)
}

# The folder itself first, then what is inside: name, dir, size, modified (UTC, ISO).
# Gives @{ Me = <entry>; Inside = <list of entries> }.
function Propfind($server, [string]$rel, [int]$depth) {
    $what = $rel
    if (-not $what) { $what = '/' }
    $r = Send-Request $server 'PROPFIND' (Server-Path $server $rel) $null @{ Depth = [string]$depth } 0 $null
    Check-Status $r.Status $what $null
    $doc = New-Object System.Xml.XmlDocument
    $doc.XmlResolver = $null
    try { $doc.LoadXml((Text-Of-Bytes $r.Data)) } catch { Fail 1 ("the phone's answer for " + $what + ' is not a WebDAV listing') }
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($response in $doc.DocumentElement.ChildNodes) {
        if ($response.LocalName -cne 'response') { continue }
        $prop = Xml-Child (Xml-Child $response 'propstat') 'prop'
        if ($null -eq $prop) { continue }
        $hrefNode = Xml-Child $response 'href'
        $href = ''
        if ($hrefNode) { $href = $hrefNode.InnerText }
        $at = $href.IndexOf('?')
        if ($at -ge 0) { $href = $href.Substring(0, $at) }
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($p in $href.Split('/')) { if ($p) { $parts.Add($p) } }
        $name = ''
        if ($parts.Count -gt 1) { $name = Nfc ([System.Uri]::UnescapeDataString($parts[$parts.Count - 1])) }
        $isDir = $null -ne (Xml-Child (Xml-Child $prop 'resourcetype') 'collection')
        $size = [long]0
        $sizeNode = Xml-Child $prop 'getcontentlength'
        if ($sizeNode -and -not $isDir) { [void][long]::TryParse($sizeNode.InnerText, [System.Globalization.NumberStyles]::Integer, $Inv, [ref]$size) }
        $modified = ''
        $dateNode = Xml-Child $prop 'getlastmodified'
        if ($dateNode) {
            $when = [datetime]::MinValue
            $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
            if ([datetime]::TryParseExact($dateNode.InnerText, "ddd, dd MMM yyyy HH:mm:ss 'GMT'", $Inv, $styles, [ref]$when)) {
                $modified = $when.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $Inv)
            }
        }
        $entries.Add(@{ name = $name; dir = $isDir; size = $size; modified = $modified })
    }
    if ($entries.Count -eq 0) { Fail 1 ("the phone's listing of " + $what + ' is empty') }
    $me = $entries[0]
    $inside = New-Object System.Collections.Generic.List[object]
    for ($i = 1; $i -lt $entries.Count; $i++) { $inside.Add($entries[$i]) }
    $inside.Sort([System.Comparison[object]] { param($a, $b) Compare-Entries $a $b })
    return @{ Me = $me; Inside = $inside }
}

function Human([double]$n) {
    foreach ($unit in @('B', 'KB', 'MB', 'GB')) {
        if ($n -lt 1024 -or $unit -eq 'GB') {
            if ($unit -eq 'B') { return ([long]$n).ToString($Inv) + ' B' }
            return (F1 $n) + ' ' + $unit
        }
        $n = $n / 1024.0
    }
}

function Entry-Line($e) {
    $when = $e.modified
    if (-not $when) { $when = '-' }
    if ($e.dir) { return 'd ' + '-'.PadLeft(12) + '  ' + $when + '  ' + $e.name + '/' }
    return 'f ' + ([long]$e.size).ToString($Inv).PadLeft(12) + '  ' + $when + '  ' + $e.name
}

# An entry as --json gives it: the same four fields as iphone.py
function Entry-Json($e) {
    $o = New-Object System.Collections.Specialized.OrderedDictionary
    $o['name'] = [string]$e.name
    $o['dir'] = [bool]$e.dir
    $o['size'] = [long]$e.size
    $o['modified'] = [string]$e.modified
    return $o
}

function Cmd-Ls($argv) {
    $a = Parse-Args $argv @('json') $null $null
    $rel = ''
    if ($a.Pos.Count -gt 0) { $rel = $a.Pos[0] }
    $listing = Propfind (Need-Server 'files') $rel 1
    $inside = $listing.Inside
    if (-not $listing.Me.dir) {
        $inside = New-Object System.Collections.Generic.List[object]
        $inside.Add($listing.Me)
    }
    if ($a.Opt.ContainsKey('json')) {
        $items = New-Object System.Collections.Generic.List[object]
        foreach ($e in $inside) { $items.Add((Entry-Json $e)) }
        Say (To-Json $items)
        return
    }
    foreach ($e in $inside) { Say (Entry-Line $e) }
    if ($inside.Count -eq 0) { Say '(empty)' }
}

# What a walk does with each entry; gives $false to stop everything.
# $ctx.Mode: tree, find, du or get; the rest of $ctx is that command's own.
function Visit($ctx, [string]$p, $e) {
    if ($ctx.Mode -eq 'tree') {
        if ($ctx.Shown -ge $ctx.Most) { return $false }
        $ctx.Shown++
        $depthOf = 0
        foreach ($x in $p.Split('/')) { if ($x) { $depthOf++ } }
        $level = $depthOf - $ctx.Base - 1
        $tail = '/'
        if (-not $e.dir) { $tail = '  (' + (Human $e.size) + ')' }
        Say (('  ' * $level) + $e.name + $tail)
        return $true
    }
    if ($ctx.Mode -eq 'find') {
        if ($ctx.Pattern.IsMatch($e.name.ToLowerInvariant())) {
            if ($ctx.Found -ge $ctx.Most) { return $false }
            $ctx.Found++
            if ($e.dir) { Say ($p + '/') } else { Say ($p + "`t" + ([long]$e.size).ToString($Inv)) }
        }
        return $true
    }
    if ($ctx.Mode -eq 'du') {
        $ctx.Seen++
        $ctx.Size += [long]$e.size
        return ($ctx.Seen -lt $ctx.Most)
    }
    if ($ctx.Mode -eq 'get') {
        $target = $ctx.Local
        foreach ($x in $p.Substring($ctx.Rel.Length).Trim('/').Split('/')) { $target = [System.IO.Path]::Combine($target, (Local-Name $x)) }
        if ($e.dir) {
            [void][System.IO.Directory]::CreateDirectory($target)
        } else {
            $ctx.Total += (Download $ctx.Server $p $target)
            $ctx.Files++
        }
        return $true
    }
    return $true
}

# Folders first to last, $depth levels down. Gives $false when a visit stopped everything.
function Walk($server, [string]$rel, [int]$depth, $ctx) {
    $listing = $null
    try {
        $listing = Propfind $server $rel 1
    } catch {
        $code = FailCode $_
        if ($code -eq 3 -or $code -eq 4) { $ctx.Unreadable.Add($rel); return $true }
        throw
    }
    foreach ($e in $listing.Inside) {
        $p = PJoin $rel $e.name
        if (-not (Visit $ctx $p $e)) { return $false }
        if ($e.dir -and $depth -gt 1) {
            if (-not (Walk $server $p ($depth - 1) $ctx)) { return $false }
        }
    }
    return $true
}

function Note-Unreadable($ctx) { foreach ($u in $ctx.Unreadable) { Note ('not readable: ' + $u) } }

function Cmd-Tree($argv) {
    $a = Parse-Args $argv $null @('depth', 'max') $null
    $rel = ''
    if ($a.Pos.Count -gt 0) { $rel = $a.Pos[0] }
    $base = 0
    foreach ($x in $rel.Split('/')) { if ($x) { $base++ } }
    $ctx = @{ Mode = 'tree'; Shown = 0; Most = (Opt-Int $a.Opt 'max' 1000); Base = $base; Unreadable = (New-Object System.Collections.Generic.List[string]) }
    $done = Walk (Need-Server 'files') $rel (Opt-Int $a.Opt 'depth' 2) $ctx
    if (-not $done) { Say ('... stopped at ' + $ctx.Most + ' entries (--max)') }
    Note-Unreadable $ctx
}

# A wildcard of fnmatch (*, ?, [abc], [!abc]) as a regular expression over the whole name
function Wildcard([string]$pattern) {
    $b = New-Object System.Text.StringBuilder
    [void]$b.Append('\A(?s:')
    $i = 0
    while ($i -lt $pattern.Length) {
        $c = $pattern[$i]
        $i++
        if ($c -eq '*') { [void]$b.Append('.*') }
        elseif ($c -eq '?') { [void]$b.Append('.') }
        elseif ($c -eq '[') {
            $j = $i
            if ($j -lt $pattern.Length -and $pattern[$j] -eq '!') { $j++ }
            if ($j -lt $pattern.Length -and $pattern[$j] -eq ']') { $j++ }
            while ($j -lt $pattern.Length -and $pattern[$j] -ne ']') { $j++ }
            if ($j -ge $pattern.Length) {
                [void]$b.Append('\[')
            } else {
                $set = $pattern.Substring($i, $j - $i).Replace('\', '\\')
                $i = $j + 1
                if ($set.StartsWith('!')) { $set = '^' + $set.Substring(1) } elseif ($set.StartsWith('^')) { $set = '\' + $set }
                [void]$b.Append('[' + $set + ']')
            }
        }
        else { [void]$b.Append([regex]::Escape([string]$c)) }
    }
    [void]$b.Append(')\z')
    return New-Object System.Text.RegularExpressions.Regex($b.ToString(), [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
}

function Cmd-Find($argv) {
    $a = Parse-Args $argv $null @('depth', 'max') $null
    if ($a.Pos.Count -ne 2) { Fail 1 'find <path> <pattern>' }
    $ctx = @{ Mode = 'find'; Found = 0; Most = (Opt-Int $a.Opt 'max' 200); Pattern = (Wildcard ((Nfc $a.Pos[1]).ToLowerInvariant())); Unreadable = (New-Object System.Collections.Generic.List[string]) }
    $done = Walk (Need-Server 'files') $a.Pos[0] (Opt-Int $a.Opt 'depth' 6) $ctx
    if (-not $done) { Say ('... stopped at ' + $ctx.Most + ' matches (--max)') }
    elseif ($ctx.Found -eq 0) { Say '(nothing)' }
    Note-Unreadable $ctx
}

function Cmd-Du($argv) {
    $a = Parse-Args $argv $null @('max') $null
    $rel = ''
    if ($a.Pos.Count -gt 0) { $rel = $a.Pos[0] }
    $most = Opt-Int $a.Opt 'max' 20000
    $server = Need-Server 'files'
    $listing = Propfind $server $rel 1
    $ctx = @{ Mode = 'du'; Seen = 0; Size = [long]0; Most = $most; Unreadable = (New-Object System.Collections.Generic.List[string]) }
    $rows = New-Object System.Collections.Generic.List[object]
    $total = [long]0
    foreach ($e in $listing.Inside) {
        $ctx.Size = [long]$e.size
        $name = $e.name
        if ($e.dir) {
            [void](Walk $server (PJoin $rel $e.name) 64 $ctx)
            $name = $e.name + '/'
        }
        $rows.Add(@{ size = [long]$ctx.Size; name = $name })
        $total += [long]$ctx.Size
    }
    $rows.Sort([System.Comparison[object]] { param($x, $y) if ($x.size -ne $y.size) { if ($x.size -gt $y.size) { -1 } else { 1 } } else { [string]::CompareOrdinal($x.name, $y.name) } })
    foreach ($r in $rows) { Say (([long]$r.size).ToString($Inv).PadLeft(12) + '  ' + (Human $r.size).PadLeft(10) + '  ' + $r.name) }
    Say ($total.ToString($Inv).PadLeft(12) + '  ' + (Human $total).PadLeft(10) + '  total')
    if ($ctx.Seen -ge $most) { Say ('... stopped after ' + $most + ' entries (--max): sizes are a lower bound') }
    Note-Unreadable $ctx
}

# The bytes of a file of the phone (a part of it with $headers). Gives the answer of Send-Request.
function Fetch($server, [string]$rel, $headers) {
    $r = Send-Request $server 'GET' (Server-Path $server $rel) $null $headers 0 $null
    if ($r.Status -eq 301) { Fail 1 ($rel + ' is a folder') }
    Check-Status $r.Status $rel $null
    return $r
}

function Text-Of($data, [string]$rel) {
    if ($data.Length -gt 0 -and [System.Array]::IndexOf($data, [byte]0, 0, [Math]::Min($data.Length, 4096)) -ge 0) {
        Fail 1 ($rel + ' is not text (download it with get, or read a property list with plist)')
    }
    return Text-Of-Bytes $data
}

function Cmd-Cat($argv) {
    $a = Parse-Args $argv $null @('tail', 'head') $null
    if ($a.Pos.Count -ne 1) { Fail 1 'cat <path> [--tail N] [--head N]' }
    $server = Need-Server 'files'
    $rel = $a.Pos[0]
    $limit = 262144
    if ($a.Opt.ContainsKey('tail')) {
        $n = Opt-Int $a.Opt 'tail' 60
        $r = Fetch $server $rel @{ 'Range-Last' = $limit }
        $lines = Split-Lines (Text-Of $r.Data $rel)
        $from = [Math]::Min($lines.Count, [Math]::Max(0, $lines.Count - $n))
        Say (($lines.GetRange($from, $lines.Count - $from)) -join "`n")
        return
    }
    $r = Fetch $server $rel @{ 'Range-First' = ($limit - 1) }
    $text = Text-Of $r.Data $rel
    if ($a.Opt.ContainsKey('head')) {
        $lines = Split-Lines $text
        $n = [Math]::Min($lines.Count, [Math]::Max(0, (Opt-Int $a.Opt 'head' 60)))
        Say (($lines.GetRange(0, $n)) -join "`n")
        return
    }
    Say ($text.TrimEnd("`n"))
    $range = [string]$r.Headers['Content-Range']
    $at = $range.LastIndexOf('/')
    if ($at -ge 0) {
        $whole = [long]0
        if ([long]::TryParse($range.Substring($at + 1), [System.Globalization.NumberStyles]::Integer, $Inv, [ref]$whole) -and $whole -gt $r.Data.Length) {
            Note ('... the first ' + $r.Data.Length + ' of ' + $whole + ' bytes (use --tail, or get the file)')
        }
    }
}

# --- property lists ---

function Be-UInt($b, [int]$at, [int]$size) {
    $v = [long]0
    for ($i = 0; $i -lt $size; $i++) { $v = ($v * 256) + [long]$b[$at + $i] }
    return $v
}

function Be-Reversed($b, [int]$at, [int]$size) {
    $x = New-Object byte[] $size
    [System.Array]::Copy($b, $at, $x, 0, $size)
    [System.Array]::Reverse($x)
    return ,$x
}

# A binary property list (bplist00) as hashtables, lists and plain values; data, dates and UIDs as iphone.py gives them
function Bplist-Object($ctx, [int]$index, [int]$deep) {
    if ($deep -gt 200) { Fail 1 'the property list is nested too deep' }
    $b = $ctx.Bytes
    $at = [int](Be-UInt $b ($ctx.Table + $index * $ctx.OffsetSize) $ctx.OffsetSize)
    $marker = $b[$at]
    $kind = $marker -shr 4
    $info = $marker -band 15
    if ($kind -eq 0) {
        if ($marker -eq 8) { return $false }
        if ($marker -eq 9) { return $true }
        return $null
    }
    if ($kind -eq 1) {
        $size = 1 -shl $info
        if ($size -ge 8) { return [System.BitConverter]::ToInt64((Be-Reversed $b ($at + 1 + $size - 8) 8), 0) }
        return [long](Be-UInt $b ($at + 1) $size)
    }
    if ($kind -eq 2) {
        if ($info -eq 2) { return [double][System.BitConverter]::ToSingle((Be-Reversed $b ($at + 1) 4), 0) }
        return [System.BitConverter]::ToDouble((Be-Reversed $b ($at + 1) 8), 0)
    }
    if ($kind -eq 3) {
        $seconds = [System.BitConverter]::ToDouble((Be-Reversed $b ($at + 1) 8), 0)
        $epoch = New-Object System.DateTime(2001, 1, 1, 0, 0, 0, [System.DateTimeKind]::Utc)
        return $epoch.AddSeconds($seconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $Inv)
    }
    if ($kind -eq 8) {
        $o = New-Object System.Collections.Specialized.OrderedDictionary
        $o['uid'] = [long](Be-UInt $b ($at + 1) ($info + 1))
        return $o
    }
    # The rest carry a count: in the marker, or (15) as an int object after it
    $count = $info
    $start = $at + 1
    if ($info -eq 15) {
        $size = 1 -shl ($b[$start] -band 15)
        $count = [int](Be-UInt $b ($start + 1) $size)
        $start = $start + 1 + $size
    }
    if ($kind -eq 4) {
        $o = New-Object System.Collections.Specialized.OrderedDictionary
        $o['data'] = [System.Convert]::ToBase64String($b, $start, $count)
        return $o
    }
    if ($kind -eq 5) { return [System.Text.Encoding]::ASCII.GetString($b, $start, $count) }
    if ($kind -eq 6) { return [System.Text.Encoding]::BigEndianUnicode.GetString($b, $start, $count * 2) }
    if ($kind -eq 7) { return $Utf8.GetString($b, $start, $count) }
    if ($kind -eq 10 -or $kind -eq 12) {
        $list = New-Object System.Collections.Generic.List[object]
        for ($i = 0; $i -lt $count; $i++) {
            $ref = [int](Be-UInt $b ($start + $i * $ctx.RefSize) $ctx.RefSize)
            $list.Add((Bplist-Object $ctx $ref ($deep + 1)))
        }
        return ,$list
    }
    if ($kind -eq 13) {
        $o = New-Object System.Collections.Specialized.OrderedDictionary
        for ($i = 0; $i -lt $count; $i++) {
            $keyRef = [int](Be-UInt $b ($start + $i * $ctx.RefSize) $ctx.RefSize)
            $valueRef = [int](Be-UInt $b ($start + ($count + $i) * $ctx.RefSize) $ctx.RefSize)
            $key = Bplist-Object $ctx $keyRef ($deep + 1)
            $o[[string]$key] = Bplist-Object $ctx $valueRef ($deep + 1)
        }
        return $o
    }
    Fail 1 'the property list has a kind of value this reader does not know'
}

# An XML property list node as the same values
function Xplist-Node($node) {
    $kind = $node.LocalName
    if ($kind -ceq 'dict') {
        $o = New-Object System.Collections.Specialized.OrderedDictionary
        $key = $null
        foreach ($c in $node.ChildNodes) {
            if ($c.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
            if ($c.LocalName -ceq 'key') { $key = $c.InnerText; continue }
            if ($null -ne $key) { $o[[string]$key] = Xplist-Node $c; $key = $null }
        }
        return $o
    }
    if ($kind -ceq 'array') {
        $list = New-Object System.Collections.Generic.List[object]
        foreach ($c in $node.ChildNodes) { if ($c.NodeType -eq [System.Xml.XmlNodeType]::Element) { $list.Add((Xplist-Node $c)) } }
        return ,$list
    }
    if ($kind -ceq 'string') { return [string]$node.InnerText }
    if ($kind -ceq 'integer') { return [long]::Parse($node.InnerText.Trim(), [System.Globalization.NumberStyles]::Integer, $Inv) }
    if ($kind -ceq 'real') { return [double]::Parse($node.InnerText.Trim(), [System.Globalization.NumberStyles]::Float, $Inv) }
    if ($kind -ceq 'true') { return $true }
    if ($kind -ceq 'false') { return $false }
    if ($kind -ceq 'date') {
        $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
        return [datetime]::Parse($node.InnerText.Trim(), $Inv, $styles).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $Inv)
    }
    if ($kind -ceq 'data') {
        $o = New-Object System.Collections.Specialized.OrderedDictionary
        $o['data'] = [System.Convert]::ToBase64String([System.Convert]::FromBase64String(($node.InnerText -replace '\s', '')))
        return $o
    }
    Fail 1 'the property list has a kind of value this reader does not know'
}

# Gives @{ Value = <the list's top value> }: wrapped, so that a list or $null comes back as it is
function Read-Plist($server, [string]$rel) {
    $r = Fetch $server $rel $null
    $b = $r.Data
    try {
        if ($b.Length -ge 40 -and [System.Text.Encoding]::ASCII.GetString($b, 0, 6) -ceq 'bplist') {
            $trailer = $b.Length - 32
            $ctx = @{ Bytes = $b; OffsetSize = [int]$b[$trailer + 6]; RefSize = [int]$b[$trailer + 7]; Table = [int](Be-UInt $b ($trailer + 24) 8) }
            $top = [int](Be-UInt $b ($trailer + 16) 8)
            return @{ Value = (Bplist-Object $ctx $top 0) }
        }
        $settings = New-Object System.Xml.XmlReaderSettings
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Ignore   # the DOCTYPE names an address at Apple: never fetched
        $settings.XmlResolver = $null
        $memory = New-Object System.IO.MemoryStream(, $b)
        $reader = [System.Xml.XmlReader]::Create($memory, $settings)
        $doc = New-Object System.Xml.XmlDocument
        $doc.XmlResolver = $null
        try { $doc.Load($reader) } finally { $reader.Close() }
        if ($doc.DocumentElement.LocalName -cne 'plist') { throw (New-Object System.Exception 'no plist') }
        foreach ($c in $doc.DocumentElement.ChildNodes) {
            if ($c.NodeType -eq [System.Xml.XmlNodeType]::Element) { return @{ Value = (Xplist-Node $c) } }
        }
        throw (New-Object System.Exception 'empty plist')
    } catch {
        Fail 1 ($rel + ' is not a property list')
    }
}

function Cmd-Plist($argv) {
    $a = Parse-Args $argv $null $null $null
    if ($a.Pos.Count -ne 1) { Fail 1 'plist <path>' }
    $got = Read-Plist (Need-Server 'files') $a.Pos[0]
    Say (To-Json-Lines $got.Value 0)
}

# The bundle id in a container's metadata, or $null
function Container-Id($server, [string]$folder) {
    try {
        $got = Read-Plist $server ($folder + '/.com.apple.mobile_container_manager.metadata.plist')
    } catch {
        if (FailCode $_) { return $null }
        throw
    }
    if ($got.Value -is [System.Collections.IDictionary] -and $got.Value.Contains('MCMMetadataIdentifier')) {
        $id = $got.Value['MCMMetadataIdentifier']
        if ($id -is [string]) { return $id }
    }
    return $null
}

function Cmd-Apps($argv) {
    $a = Parse-Args $argv @('groups') $null $null
    $wanted = ''
    if ($a.Pos.Count -gt 0) { $wanted = $a.Pos[0].ToLowerInvariant() }
    $server = Need-Server 'files'
    $base = 'Containers/Data/Application'
    if ($a.Opt.ContainsKey('groups')) { $base = 'Containers/Shared/AppGroup' }
    $listing = $null
    try {
        $listing = Propfind $server $base 1
    } catch {
        if ((FailCode $_) -eq 4) { Fail 4 ($base + " is not there: this build shares only LiteBro's own folder (LiteBro-Browser.tipa)") }
        throw
    }
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($e in $listing.Inside) {
        if (-not $e.dir) { continue }
        $p = PJoin $base $e.name
        $name = Container-Id $server $p
        if ($null -eq $name) { $name = '?' }
        if ($name.ToLowerInvariant().Contains($wanted)) { $rows.Add(@{ key = $name.ToLowerInvariant(); name = $name; path = $p }) }
    }
    $rows.Sort([System.Comparison[object]] { param($x, $y)
        $c = [string]::CompareOrdinal($x.key, $y.key)
        if ($c -ne 0) { $c } else { $c = [string]::CompareOrdinal($x.name, $y.name); if ($c -ne 0) { $c } else { [string]::CompareOrdinal($x.path, $y.path) } }
    })
    foreach ($r in $rows) { Say ($r.name + "`t" + $r.path) }
    if ($rows.Count -eq 0) { Say '(nothing)' }
}

# --- files to and from this computer ---

# Gives the number of bytes saved
function Download($server, [string]$rel, [string]$local) {
    $r = $null
    try {
        $r = Send-Request $server 'GET' (Server-Path $server $rel) $null $null 30 $local
    } catch {
        $e = $_.Exception
        while ($e -and -not $e.Data.Contains('broke')) { $e = $e.InnerException }
        if ($e) { Fail 2 ($rel + ' broke off after ' + $e.Data['after'] + ' bytes: ' + $e.Data['broke']) }
        throw
    }
    if ($r.Status -ne 200) {
        if ($r.Status -eq 301) { Fail 1 ($rel + ' is a folder: add -r') }
        Check-Status $r.Status $rel $null
    }
    $size = [long]$r.Data
    $expected = [string]$r.Headers['Content-Length']
    $want = [long]0
    if ($expected -and [long]::TryParse($expected, [System.Globalization.NumberStyles]::Integer, $Inv, [ref]$want) -and $want -ne $size) {
        Fail 2 ($rel + ' broke off: ' + $size + ' of ' + $expected + ' bytes')
    }
    return $size
}

# A name from the phone as this computer can hold it (Windows forbids some characters)
function Local-Name([string]$name) {
    if ([System.IO.Path]::DirectorySeparatorChar -ne '\') { return $name }
    $b = New-Object System.Text.StringBuilder
    foreach ($c in $name.ToCharArray()) {
        if ('<>:"/\|?*'.IndexOf($c) -ge 0 -or [int]$c -lt 32) { [void]$b.Append('_') } else { [void]$b.Append($c) }
    }
    $clean = $b.ToString().TrimEnd('.', ' ')
    if (-not $clean) { $clean = '_' }
    return $clean
}

function Cmd-Get($argv) {
    $a = Parse-Args $argv @('recursive') $null $null
    if ($a.Pos.Count -lt 1 -or $a.Pos.Count -gt 2) { Fail 1 'get <remote> [local] [-r]' }
    $server = Need-Server 'files'
    $rel = $a.Pos[0].Trim('/')
    $me = (Propfind $server $rel 0).Me
    $name = $me.name
    if (-not $name) { $name = 'phone' }
    $name = Local-Name $name
    $local = $name
    if ($a.Pos.Count -gt 1) { $local = $a.Pos[1] }
    $full = Local-Path $local
    if (-not $me.dir) {
        if ([System.IO.Directory]::Exists($full)) {
            $local = [System.IO.Path]::Combine($local, $name)
            $full = [System.IO.Path]::Combine($full, $name)
        }
        $size = Download $server $rel $full
        Say ('saved ' + $local + ' (' + $size + ' bytes)')
        return
    }
    if (-not $a.Opt.ContainsKey('recursive')) { Fail 1 ($rel + ' is a folder: add -r') }
    [void][System.IO.Directory]::CreateDirectory($full)
    $ctx = @{ Mode = 'get'; Server = $server; Rel = $rel; Local = $full; Files = 0; Total = [long]0; Unreadable = (New-Object System.Collections.Generic.List[string]) }
    [void](Walk $server $rel 64 $ctx)
    Say ('saved ' + $local + ' (' + $ctx.Files + ' files, ' + $ctx.Total + ' bytes)')
    Note-Unreadable $ctx
}

# Gives the number of bytes sent
function Upload($server, [string]$full, [string]$rel) {
    $size = (New-Object System.IO.FileInfo $full).Length
    $r = Send-Request $server 'PUT' (Server-Path $server $rel) @{ File = $full } $null 60 $null
    if ($r.Status -eq 409) { Fail 4 ($rel + ': the folder to put it in is not there (409)') }
    Check-Status $r.Status $rel $null
    return $size
}

# 1: a folder, 0: a file, -1: not there
function Folder-State($server, [string]$rel) {
    try {
        if ((Propfind $server $rel 0).Me.dir) { return 1 }
        return 0
    } catch {
        if ((FailCode $_) -eq 4) { return -1 }
        throw
    }
}

function Make-Folders($server, [string]$rel) {
    $made = ''
    foreach ($part in $rel.Replace('\', '/').Split('/')) {
        if (-not $part) { continue }
        $made = PJoin $made $part
        $r = Send-Request $server 'MKCOL' (Server-Path $server $made) $null $null 0 $null
        if ($r.Status -ne 201 -and $r.Status -ne 405) { Check-Status $r.Status $made $null }   # 405: it is there already
    }
}

function Sorted-Names($paths) {
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($p in $paths) { $names.Add([System.IO.Path]::GetFileName($p)) }
    $names.Sort([System.StringComparer]::Ordinal)
    return ,$names
}

# A folder of this computer to the phone, as os.walk goes: the files of a folder, then its folders in order
function Upload-Folder($server, [string]$full, [string]$rel, $ctx) {
    foreach ($f in (Sorted-Names ([System.IO.Directory]::GetFiles($full)))) {
        $ctx.Total += (Upload $server ([System.IO.Path]::Combine($full, $f)) (PJoin $rel $f))
        $ctx.Files++
    }
    foreach ($d in (Sorted-Names ([System.IO.Directory]::GetDirectories($full)))) {
        Make-Folders $server (PJoin $rel $d)
        Upload-Folder $server ([System.IO.Path]::Combine($full, $d)) (PJoin $rel $d) $ctx
    }
}

function Cmd-Put($argv) {
    $a = Parse-Args $argv @('recursive') $null $null
    if ($a.Pos.Count -ne 2) { Fail 1 'put <local> <remote> [-r]' }
    $local = $a.Pos[0]
    $rel = $a.Pos[1]
    $server = Need-Server 'files'
    $full = Local-Path $local
    $isFile = [System.IO.File]::Exists($full)
    if (-not $isFile -and -not [System.IO.Directory]::Exists($full)) { Fail 1 ('no such file here: ' + $local) }
    $into = $rel.EndsWith('/') -or $rel.EndsWith('\')
    if (-not $into) { $into = ((Folder-State $server $rel) -eq 1) }
    $rel = $rel.Trim('/')
    if ($into) { $rel = PJoin $rel ([System.IO.Path]::GetFileName($full.TrimEnd('\', '/'))) }
    if ($isFile) {
        $size = Upload $server $full $rel
        Say ('uploaded ' + $rel + ' (' + $size + ' bytes)')
        return
    }
    if (-not $a.Opt.ContainsKey('recursive')) { Fail 1 ($local + ' is a folder: add -r') }
    $ctx = @{ Files = 0; Total = [long]0 }
    Make-Folders $server $rel
    Upload-Folder $server $full $rel $ctx
    Say ('uploaded ' + $rel + ' (' + $ctx.Files + ' files, ' + $ctx.Total + ' bytes)')
}

function Cmd-Mkdir($argv) {
    $a = Parse-Args $argv $null $null $null
    if ($a.Pos.Count -ne 1) { Fail 1 'mkdir <remote>' }
    Make-Folders (Need-Server 'files') $a.Pos[0]
    Say ('made ' + $a.Pos[0].Trim('/'))
}

function Cmd-Rm($argv) {
    $a = Parse-Args $argv @('recursive') $null $null
    if ($a.Pos.Count -ne 1 -or -not $a.Pos[0].Trim('/')) { Fail 1 'rm <remote> [-r]' }
    $server = Need-Server 'files'
    $rel = $a.Pos[0].Trim('/')
    $folder = Folder-State $server $rel
    if ($folder -lt 0) { Fail 4 ($rel + ': not found (404)') }
    if ($folder -eq 1 -and -not $a.Opt.ContainsKey('recursive')) { Fail 1 ($rel + ' is a folder: rm -r deletes it with everything inside, for good') }
    $r = Send-Request $server 'DELETE' (Server-Path $server $rel) $null $null 0 $null
    Check-Status $r.Status $rel $null
    Say ('deleted ' + $rel)
}

function Cmd-Move($argv, [string]$method, [string]$done) {
    $a = Parse-Args $argv @('force') $null $null
    if ($a.Pos.Count -ne 2) { Fail 1 ($done + ' <from> <to> [--force]') }
    $server = Need-Server 'files'
    $src = $a.Pos[0].Trim('/')
    $dst = $a.Pos[1]
    if ((Folder-State $server $src) -lt 0) { Fail 4 ($src + ': not found (404)') }
    $into = $dst.EndsWith('/')
    if (-not $into) { $into = ((Folder-State $server $dst) -eq 1) }
    if ($into) {
        $parts = $src.Split('/')
        $dst = PJoin $dst $parts[$parts.Length - 1]
    }
    $dst = $dst.Trim('/')
    $overwrite = 'F'
    if ($a.Opt.ContainsKey('force')) { $overwrite = 'T' }
    $r = Send-Request $server $method (Server-Path $server $src) $null @{ Destination = (Server-Path $server $dst); Overwrite = $overwrite } 0 $null
    if ($r.Status -eq 412) { Fail 1 ($dst + ' is there already (--force replaces it)') }
    if ($r.Status -eq 409) { Fail 4 ($dst + ': the folder to put it in is not there (409)') }
    Check-Status $r.Status $src $null
    $word = 'copied'
    if ($method -eq 'MOVE') { $word = 'moved' }
    Say ($word + ' ' + $src + ' -> ' + $dst)
}

# --- the screen ---

# The pictures of the screen as the broadcast sends them (multipart/x-mixed-replace), over a plain socket.
# $s: @{ Server; Tmp } and, once open, Client, Sock, Mem (what has come), Off (how much of it is used up).
function Stream-Open($s) {
    $server = $s.Server
    $where = 'no answer from ' + $server.Host + ':' + $server.Port + ': '
    $client = New-Object System.Net.Sockets.TcpClient
    $client.NoDelay = $true
    $pending = $client.BeginConnect([string]$server.Host, [int]$server.Port, $null, $null)
    if (-not $pending.AsyncWaitHandle.WaitOne([int]($server.Timeout * 1000))) {
        $client.Close()
        Fail 2 ($where + $SilentWords)
    }
    try {
        $client.EndConnect($pending)
    } catch {
        $client.Close()
        $e = $_.Exception
        while ($e.InnerException) { $e = $e.InnerException }
        if ($e -is [System.Net.Sockets.SocketException] -and $e.SocketErrorCode -eq [System.Net.Sockets.SocketError]::ConnectionRefused) {
            Fail-Refused ($where + 'refused (the screen broadcast is not running)')
        }
        if ($e -is [System.Net.Sockets.SocketException] -and $e.SocketErrorCode -eq [System.Net.Sockets.SocketError]::TimedOut) { Fail 2 ($where + $SilentWords) }
        Fail 2 ($where + $e.Message)
    }
    $s.Client = $client
    $s.Sock = $client.Client
    $s.Mem = New-Object System.IO.MemoryStream
    $s.Off = 0
    $ask = 'GET ' + $server.Base + "stream HTTP/1.1`r`nHost: " + $server.Host + ':' + $server.Port + "`r`nConnection: close`r`n`r`n"
    [void]$s.Sock.Send([System.Text.Encoding]::ASCII.GetBytes($ask))
    $head = Stream-Head $s ((Now) + $server.Timeout)
    if ($null -eq $head) {
        Stream-Close $s
        Fail 2 ($where + 'the stream did not start')
    }
    $code = 0
    $words = $head.Split("`r`n".ToCharArray())[0].Split(' ')
    if ($words.Length -gt 1) { [void][int]::TryParse($words[1], [System.Globalization.NumberStyles]::Integer, $Inv, [ref]$code) }
    if ($code -ne 200) {
        Stream-Close $s
        Check-Status $code 'the screen address' $null
        Fail 1 ('the screen address: the phone answered ' + $code)
    }
}

function Stream-Close($s) {
    if ($s.Client) { try { $s.Client.Close() } catch { } }
    $s.Client = $null
    $s.Sock = $null
}

# More bytes into the buffer. 1: some came. 0: the time is out. -1: the phone ended the stream.
function Stream-Fill($s, [double]$deadline) {
    $left = $deadline - (Now)
    if ($left -le 0) { return 0 }
    $micro = [int][Math]::Min($left * 1000000.0, 2000000000.0)
    $ready = $false
    try { $ready = $s.Sock.Poll($micro, [System.Net.Sockets.SelectMode]::SelectRead) } catch { return -1 }
    if (-not $ready) { return 0 }
    $n = 0
    try { $n = $s.Sock.Receive($s.Tmp) } catch { $n = 0 }
    if ($n -le 0) { return -1 }
    $s.Mem.Position = $s.Mem.Length
    $s.Mem.Write($s.Tmp, 0, $n)
    return 1
}

function Stream-Drop($s, [int]$count) {
    $s.Off = $s.Off + $count
    if ($s.Off -ge $s.Mem.Length) {
        $s.Mem.SetLength(0)
        $s.Off = 0
    } elseif ($s.Off -gt 4194304) {
        # Pictures come faster than they are read: what is read already is let go
        $rest = [int]($s.Mem.Length - $s.Off)
        $keep = New-Object byte[] $rest
        [System.Array]::Copy($s.Mem.GetBuffer(), $s.Off, $keep, 0, $rest)
        $s.Mem.SetLength(0)
        $s.Mem.Write($keep, 0, $rest)
        $s.Off = 0
    }
}

# The lines before an empty line, taken out of the buffer; $null when they did not come (see Stream-Fill for why: $s.Why)
function Stream-Head($s, [double]$deadline) {
    while ($true) {
        $have = [int]($s.Mem.Length - $s.Off)
        # Line ends left from the part before
        while ($have -ge 2 -and $s.Mem.GetBuffer()[$s.Off] -eq 13 -and $s.Mem.GetBuffer()[$s.Off + 1] -eq 10) {
            Stream-Drop $s 2
            $have = [int]($s.Mem.Length - $s.Off)
        }
        if ($have -gt 0) {
            $text = [System.Text.Encoding]::ASCII.GetString($s.Mem.GetBuffer(), $s.Off, [Math]::Min($have, 4096))
            $at = $text.IndexOf("`r`n`r`n", [System.StringComparison]::Ordinal)
            if ($at -ge 0) {
                Stream-Drop $s ($at + 4)
                return $text.Substring(0, $at)
            }
        }
        $s.Why = Stream-Fill $s $deadline
        if ($s.Why -ne 1) { return $null }
    }
}

# The next picture: @{ Kind = 'picture'; Picture }, or Kind 'timeout' (none within the time), or 'ended' (the phone
# ended the stream)
function Stream-Frame($s, [double]$timeout) {
    $deadline = (Now) + $timeout
    $head = Stream-Head $s $deadline
    if ($null -eq $head) {
        if ($s.Why -eq 0) { return @{ Kind = 'timeout' } }
        return @{ Kind = 'ended' }
    }
    $length = -1
    foreach ($line in $head.Split("`n")) {
        $l = $line.Trim()
        if ($l.ToLowerInvariant().StartsWith('content-length:')) {
            [void][int]::TryParse($l.Substring(15).Trim(), [System.Globalization.NumberStyles]::Integer, $Inv, [ref]$length)
        }
    }
    if ($length -lt 0) { return @{ Kind = 'ended' } }
    while (($s.Mem.Length - $s.Off) -lt $length) {
        $got = Stream-Fill $s ($deadline + 5)   # a picture that began is read to its end
        if ($got -eq 0) { return @{ Kind = 'timeout' } }
        if ($got -lt 0) { return @{ Kind = 'ended' } }
    }
    $picture = New-Object byte[] $length
    [System.Array]::Copy($s.Mem.GetBuffer(), $s.Off, $picture, 0, $length)
    Stream-Drop $s $length
    return @{ Kind = 'picture'; Picture = $picture }
}

# Width and height from the JPEG's frame header, as @{ w; h }
function Jpeg-Size($data) {
    $i = 2
    while ($i + 9 -lt $data.Length) {
        if ($data[$i] -ne 255) { $i++; continue }
        $marker = [int]$data[$i + 1]
        if ($marker -eq 216 -or $marker -eq 1 -or ($marker -ge 208 -and $marker -le 215)) { $i += 2; continue }
        if ($marker -eq 255) { $i++; continue }
        $size = ([int]$data[$i + 2] * 256) + [int]$data[$i + 3]
        if ($marker -ge 192 -and $marker -le 207 -and $marker -ne 196 -and $marker -ne 200 -and $marker -ne 204) {
            return @{ w = ([int]$data[$i + 7] * 256) + [int]$data[$i + 8]; h = ([int]$data[$i + 5] * 256) + [int]$data[$i + 6] }
        }
        $i += 2 + $size
    }
    return @{ w = 0; h = 0 }
}

function Same-Bytes($a, $b) {
    if ($a.Length -ne $b.Length) { return $false }
    return ([System.Convert]::ToBase64String($a) -ceq [System.Convert]::ToBase64String($b))
}

# An action is a list of steps: a request of a touch, a test command of the phone, or a pause.
# The first answer of the phone is left in $box.Met.
function Run-Steps($steps, $box) {
    foreach ($step in $steps) {
        if ($step.Kind -eq 'sleep') {
            Wait-Seconds $step.Seconds
        } elseif ($step.Kind -eq 'touch') {
            $met = Post-Touch $step.Server $step.Name $step.Payload
            if (-not $box.Has) { $box.Met = $met; $box.Has = $true }
        } elseif ($step.Kind -eq 'remote') {
            $r = Remote-Call $step.Name $step.Payload 0
            if (-not $box.Has) { $box.Met = $r.Answer; $box.Has = $true }
        }
    }
}

# One more picture for "watch": saved and printed. Gives $true when the watch has seen enough.
function Watch-Frame($watch, $picture, [bool]$wasThere) {
    $watch.Shown++
    $path = [System.IO.Path]::Combine([string]$watch.Folder, ('frame-' + ([int]$watch.Shown).ToString('000', $Inv) + '.jpg'))
    $size = Save-Shot $picture $path
    $mark = ''
    if ($wasThere) { $mark = '  ' + $watch.First }
    Say (((Now) - $watch.Start).ToString('F2', $Inv).PadLeft(6) + '  ' + $path + '  ' + $size.w + 'x' + $size.h + $mark)
    return ([bool]$watch.Until -and -not $wasThere)
}

# Watches the screen around an action and gives the last picture: @{ Picture; New; Kept; Stopped }.
#
# The phone encodes pictures only while somebody watches and only when the screen changes, and a new
# watcher first gets the picture kept from the last time: it can be older than the screen. So the
# stream is opened first, the kept picture is taken, then the action runs, and the pictures that come
# after it are the truth. New: how many came after the kept one. Kept: was one kept.
#
# The first live picture is waited for $react s after an action, $settle s when a kept one came,
# else $wait s; after each one, $settle s more; $total s at most (below 0: the first wait + 5).
function Observe($server, $steps, $box, [double]$wait, [double]$react, [double]$settle, [double]$total, $watch) {
    $s = @{ Server = $server; Tmp = (New-Object byte[] 262144); Why = 0 }
    Stream-Open $s
    try {
        $last = $null
        $f = Stream-Frame $s $KeptWait
        if ($f.Kind -eq 'picture') { $last = $f.Picture }
        $kept = ($null -ne $last)
        $stopped = $false
        if ($kept -and $null -ne $watch) { $stopped = Watch-Frame $watch $last $true }
        if ($null -ne $steps -and -not $stopped) { Run-Steps $steps $box }
        $start = Now
        $first = $wait
        if ($null -ne $steps) { $first = $react } elseif ($kept) { $first = $settle }
        $end = $start + $first + 5.0
        if ($total -ge 0) { $end = $start + $total }
        $quiet = $start + $first
        $new = 0
        while (-not $stopped) {
            $left = [Math]::Min($quiet, $end) - (Now)
            if ($left -le 0) { break }
            $f = Stream-Frame $s $left
            if ($f.Kind -eq 'timeout') { break }
            if ($f.Kind -eq 'ended') {
                # The phone ends the stream after 10 s without a change: watch again while there is time.
                # It then sends its kept picture once more; that is news only if it is another one.
                Stream-Close $s
                Stream-Open $s
                $again = [Math]::Min($KeptWait, [Math]::Max(0.05, [Math]::Min($quiet, $end) - (Now)))
                $f = Stream-Frame $s $again
                if ($f.Kind -ne 'picture') { continue }
                if ($null -ne $last -and (Same-Bytes $f.Picture $last)) { continue }
            }
            $last = $f.Picture
            $new++
            if ($null -ne $watch) { $stopped = Watch-Frame $watch $last $false }
            $quiet = (Now) + $settle
        }
        return @{ Picture = $last; New = $new; Kept = $kept; Stopped = $stopped }
    } finally {
        Stream-Close $s
    }
}

# The screen as it is now, from a build that always keeps its newest frame (GET frame).
# $null: the phone has no such address (an older build: see Observe). Else @{ Picture; Number }: no picture when
# nothing is newer than $after (below 0: not asked), or when iOS has given the phone no frame yet (number 0).
function Get-Frame($server, [int]$after) {
    $query = ''
    if ($after -ge 0) { $query = '?after=' + $after }
    $r = Send-Request $server 'GET' ($server.Base + 'frame' + $query) $null $null 0 $null
    if ($r.Status -eq 404) { return $null }
    if ($r.Status -eq 503) { return @{ Picture = $null; Number = 0 } }
    Check-Status $r.Status 'the screen address' @(200, 204)
    $number = 0
    [void][int]::TryParse([string]$r.Headers['X-Frame'], [System.Globalization.NumberStyles]::Integer, $Inv, [ref]$number)
    $picture = $null
    if ($r.Status -eq 200) { $picture = $r.Data }
    return @{ Picture = $picture; Number = $number }
}

# Observe for a build with GET frame: the phone knows its screen, so it is asked.
# Gives $null, before the action is run, when the build has no GET frame. With $watch the screen as it is at the
# start is reported too. The times are those of Observe.
function Follow($server, $steps, $box, [double]$wait, [double]$react, [double]$settle, [double]$total, $watch) {
    $got = $null
    if ($null -ne $watch) { $got = Get-Frame $server (-1) } else { $got = Get-Frame $server $NeverNumber }
    if ($null -eq $got) { return $null }
    $last = $null
    $number = [int]$got.Number
    $stopped = $false
    if ($null -ne $watch) {
        $last = $got.Picture
        if ($null -ne $last) { $stopped = Watch-Frame $watch $last $true }
    }
    if ($null -ne $steps -and -not $stopped) { Run-Steps $steps $box }
    $start = Now
    $first = $wait
    if ($null -ne $steps) { $first = $react }
    $end = $start + $first + 5.0
    if ($total -ge 0) { $end = $start + $total }
    $quiet = $start + $first
    $new = 0
    while (-not $stopped) {
        $left = [Math]::Min($quiet, $end) - (Now)
        if ($left -le 0) { break }
        Wait-Seconds ([Math]::Min($PollWait, $left))
        $got = Get-Frame $server $number
        if ($null -eq $got) { break }
        if ($null -ne $got.Picture -and [int]$got.Number -gt $number) {
            $last = $got.Picture
            $number = [int]$got.Number
            $new++
            if ($null -ne $watch) { $stopped = Watch-Frame $watch $last $false }
            $quiet = (Now) + $settle
        }
    }
    if ($null -eq $last) {
        $got = Get-Frame $server (-1)
        if ($null -ne $got) { $last = $got.Picture }
    }
    return @{ Picture = $last; New = $new; Kept = $false; Stopped = $stopped }
}

function Shot-Path($opt, $pos) {
    if ($pos -and $pos.Count -gt 0 -and $pos[0]) { return [string]$pos[0] }
    if ($opt.ContainsKey('out') -and $opt['out']) { return [string]$opt['out'] }
    return [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'iphone-shot.jpg')
}

# Writes the picture and remembers its size: pixels given to tap are of it. Gives @{ w; h }.
function Save-Shot($picture, [string]$path) {
    [System.IO.File]::WriteAllBytes((Local-Path $path), [byte[]]$picture)
    $size = Jpeg-Size $picture
    $state = Load-State
    $state['shot'] = @{ w = $size.w; h = $size.h }
    Save-State $state
    return $size
}

# LiteBro's own screen as a JPEG, made by the app itself (the phone's command shot): no broadcast is needed.
# $null: this build of LiteBro has no such command. Else @{ Picture }.
function App-Shot($files) {
    $r = Send-Request $files 'POST' ($files.Base + '_remote/shot') ($Utf8.GetBytes('{}')) @{ 'Content-Type' = 'application/json' } 0 $null
    if ($r.Status -eq 403) { Fail 3 'the phone refused (403): touch control is not switched on with the screen permission, or the permission ran out' }
    if ($r.Status -eq 405 -or ($r.Status -eq 404 -and (Text-Of-Bytes $r.Data).TrimStart().StartsWith('{'))) { return $null }
    if ($r.Status -eq 409) { Fail 5 'LiteBro has no window to picture right now' }
    Check-Status $r.Status 'shot' @(200)
    return @{ Picture = $r.Data }
}

# shot --app: LiteBro's own screen, pictured by the app
function Own-Screen($opt, $pos, [string]$why) {
    $own = App-Shot (Need-Server 'files')
    if ($null -eq $own) { Fail 1 $NoOwn }
    $path = Shot-Path $opt $pos
    $size = Save-Shot $own.Picture $path
    Say ('shot ' + $path + ' ' + $size.w + 'x' + $size.h + ' app')
    if ($why) { Note $why }
}

function Cmd-Shot($argv) {
    $a = Parse-Args $argv @('app') @('wait', 'out') $null
    $wait = Opt-Number $a.Opt 'wait' 12.0
    $state = Load-State
    if ($a.Opt.ContainsKey('app') -or ($state.ContainsKey('files') -and -not $state.ContainsKey('screen'))) {
        Own-Screen $a.Opt $a.Pos ''
        return
    }
    $screen = Need-Server 'screen'
    $got = $null
    try {
        $got = Get-Frame $screen (-1)
    } catch {
        # The phone is there and the broadcast is not: LiteBro's own screen, if this build can picture it
        if (-not ((Is-Refused $_) -and $state.ContainsKey('files'))) { throw }
        $first = $_
        try {
            Own-Screen $a.Opt $a.Pos "note: the screen broadcast does not answer; this is LiteBro's own screen, pictured by the app."
        } catch {
            if (-not (FailCode $_)) { throw }
            throw $first
        }
        return
    }
    if ($null -ne $got) {
        $deadline = (Now) + $wait
        while ($null -eq $got.Picture -and (Now) -lt $deadline) {
            Wait-Seconds 0.3   # the broadcast has only begun: iOS has given no frame yet
            $got = Get-Frame $screen (-1)
            if ($null -eq $got) { $got = @{ Picture = $null; Number = 0 } }
        }
        if ($null -eq $got.Picture) { Fail 5 ('no picture in ' + (G $wait) + ' s: the broadcast has only begun and iOS has given the phone no frame yet') }
        $path = Shot-Path $a.Opt $a.Pos
        $size = Save-Shot $got.Picture $path
        Say ('shot ' + $path + ' ' + $size.w + 'x' + $size.h + ' live')
        return
    }
    $seen = Observe $screen $null $null $wait 2.5 0.6 (-1) $null
    if ($null -eq $seen.Picture) {
        Fail 5 ('no picture in ' + (G $wait) + ' s: the broadcast runs, but this build of LiteBro sends a picture only when the screen changes while somebody watches. Do the next action with --shot, or have the screen touched.')
    }
    $path = Shot-Path $a.Opt $a.Pos
    $size = Save-Shot $seen.Picture $path
    $word = 'kept'
    if ($seen.New -gt 0 -or -not $seen.Kept) { $word = 'live' }
    Say ('shot ' + $path + ' ' + $size.w + 'x' + $size.h + ' ' + $word)
    if ($seen.Kept -and $seen.New -eq 0) {
        Note 'note: this is the picture the phone kept from the last time somebody watched; the screen did not change while watching now. If it changed in between, the picture is older than the screen.'
    }
}

function Cmd-Watch($argv) {
    $a = Parse-Args $argv @('until-change') @('seconds') $null
    $seconds = Opt-Number $a.Opt 'seconds' 10.0
    $folder = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'iphone-watch')
    if ($a.Pos.Count -gt 0) { $folder = $a.Pos[0] }
    [void][System.IO.Directory]::CreateDirectory((Local-Path $folder))
    # First: what the first picture is: the screen now, or (an older build) the one the phone kept
    $watch = @{ Folder = $folder; Shown = 0; Start = (Now); First = 'now'; Until = $a.Opt.ContainsKey('until-change') }
    $screen = Need-Server 'screen'
    $seen = Follow $screen $null $null $seconds 2.5 $seconds $seconds $watch
    if ($null -eq $seen) {
        $watch.First = 'kept'
        $seen = Observe $screen $null $null $seconds 2.5 $seconds $seconds $watch
    }
    if ($seen.Stopped) { return }
    if ($watch.Until) { Fail 5 ('the screen did not change in ' + (G $seconds) + ' s') }
    if ($watch.Shown -eq 0) { Fail 5 ('no picture in ' + (G $seconds) + ' s') }
}

# --- taps and text ---

# Where touch requests go: the app (it reaches the HID system and its own windows) or the broadcast extension.
# Gives @{ Server; Name }.
function Touch-Target($opt) {
    $via = 'app'
    if ($opt.ContainsKey('via')) { $via = $opt['via'] }
    if ($via -cne 'app' -and $via -cne 'screen') { Fail 1 '--via app or --via screen' }
    if ($via -ceq 'app') { return @{ Server = (Need-Server 'files'); Name = '_touch' } }
    return @{ Server = (Need-Server 'screen'); Name = 'touch' }
}

# Sends one touch request (an ordered dictionary). Gives what an in-app finger met, or $null.
function Post-Touch($server, [string]$name, $payload) {
    foreach ($key in @('index', 'identity')) {
        if ($payload.Contains($key)) {
            $v = Number-Of $payload[$key]
            if ($null -ne $v -and ($v -lt 0 -or $v -gt 4294967295)) { Fail 1 ($key + ' must be 0..4294967295: an older build of LiteBro crashes on anything else') }
        }
    }
    $r = Send-Request $server 'POST' ($server.Base + $name) ($Utf8.GetBytes((To-Json $payload))) @{ 'Content-Type' = 'application/json' } 0 $null
    if ($r.Status -eq 403) {
        Fail 3 'the phone refused the touch (403): touch control is not switched on with the screen permission, or the permission ran out'
    }
    if ($r.Status -eq 200) {
        # An in-app finger is answered with what it met. The report instead means the phone could not read
        # the request (text cut inside a character?) and did nothing.
        $got = From-Json (Text-Of-Bytes $r.Data)
        if (-not $got.Ok -or -not (Is-Object $got.Value) -or (Has-Field $got.Value 'available')) {
            Fail 1 'the phone did not understand the touch request and did nothing'
        }
        $problem = Field $got.Value 'error'
        if ($problem) { Fail 1 ('the phone could not make the touch: ' + $problem) }
        return $got.Value
    }
    Check-Status $r.Status 'touch' @(204)
    return $null
}

function Coordinate([string]$text, [string]$side, $opt) {
    $v = 0.0
    if (-not [double]::TryParse($text, [System.Globalization.NumberStyles]::Float, $Inv, [ref]$v)) { Fail 1 ('not a coordinate: ' + $text) }
    if ($v -ge 0 -and $v -le 1) { return $v }
    $size = $null
    if ($opt.ContainsKey('of')) {
        $wh = ([string]$opt['of']).ToLowerInvariant().Split('x')
        $w = 0.0
        $h = 0.0
        if ($wh.Length -ne 2 -or -not [double]::TryParse($wh[0], [System.Globalization.NumberStyles]::Float, $Inv, [ref]$w) -or -not [double]::TryParse($wh[1], [System.Globalization.NumberStyles]::Float, $Inv, [ref]$h)) {
            Fail 1 '--of takes WIDTHxHEIGHT'
        }
        $size = @{ w = $w; h = $h }
    } else {
        $state = Load-State
        if ($state.ContainsKey('shot')) { $size = $state['shot'] }
    }
    if ($null -eq $size -or -not $size[$side]) {
        Fail 1 'pixels need a picture to be of: take a shot first, or add --of WIDTHxHEIGHT (or give 0..1)'
    }
    return [Math]::Min([Math]::Max($v / [double]$size[$side], 0.0), 1.0)
}

# Inside LiteBro: its own event queue. System-wide: made the way TrollVNC makes it (--opt recipe=none: the way
# LiteBro made it before); a build that does not know the recipe ignores the key.
function Finger-Options($opt) {
    $o = New-Object System.Collections.Specialized.OrderedDictionary
    if ($opt.ContainsKey('system')) { $o['recipe'] = 'trollvnc' } else { $o['inApp'] = $true }
    if ($opt.ContainsKey('opt')) { Add-Pairs $o $opt['opt'] }
    return $o
}

function Finger-Step($target, $extra, [string]$t, [double]$x, [double]$y) {
    $o = New-Object System.Collections.Specialized.OrderedDictionary
    foreach ($k in @($extra.get_Keys())) { $o[[string]$k] = $extra[[string]$k] }
    $o['t'] = $t
    $o['x'] = $x
    $o['y'] = $y
    return @{ Kind = 'touch'; Server = $target.Server; Name = $target.Name; Payload = $o }
}

function Text-Step($server, [string]$text) {
    $o = New-Object System.Collections.Specialized.OrderedDictionary
    $o['type'] = $text
    return @{ Kind = 'touch'; Server = $server; Name = '_touch'; Payload = $o }
}

# What to add to the line of an action once it is done
function More-Text([string]$kind, $box) {
    $m = $box.Met
    if ($kind -eq 'finger') {
        # A build that answers an in-app finger says what was under it
        if (-not (Is-Object $m) -or @($m.get_Keys()).Count -eq 0) { return '' }
        $window = Field $m 'window'
        $enqueue = Field $m 'enqueue'
        if (($window -is [bool] -and -not $window) -or ($enqueue -is [bool] -and -not $enqueue)) { return ', but LiteBro had no window to take it' }
        $hit = Field $m 'hit'
        if (-not $hit) { $hit = 'nothing' }
        return ', under the finger: ' + $hit
    }
    if ($kind -eq 'click') {
        $hit = Field $m 'hit'
        if (-not $hit) { $hit = 'nothing' }
        $control = Field $m 'control'
        if (-not $control) { $control = 'none' }
        return ', under the point: ' + $hit + ', control: ' + $control
    }
    return ''
}

# Runs the action; with --shot watches the screen around it and saves what it became.
# --shot without the screen broadcast: LiteBro pictures its own screen before and after the action
function Acted-Own($opt, $steps, [string]$label, [string]$more, $box, $before) {
    $files = Need-Server 'files'
    if ($null -eq $before) { $before = App-Shot $files }
    if ($null -eq $before) { Fail 1 $NoOwn }
    Run-Steps $steps $box
    Wait-Seconds $OwnSettle
    Say ($label + ' sent' + (More-Text $more $box))
    $after = $null
    try {
        $after = App-Shot $files
    } catch {
        if (-not (FailCode $_)) { throw }
        Say 'shot none changed=no'
        Note ('note: no picture came after the action: ' + (FailText $_))
        return
    }
    if ($null -eq $after) {
        Say 'shot none changed=no'
        Note 'note: no picture came after the action.'
        return
    }
    $path = Shot-Path $opt $null
    $size = Save-Shot $after.Picture $path
    $same = Same-Bytes $after.Picture $before.Picture
    $changed = 'yes'
    if ($same) { $changed = 'no' }
    Say ('shot ' + $path + ' ' + $size.w + 'x' + $size.h + ' changed=' + $changed + ' app')
    if ($same) { Note ("note: LiteBro's own screen did not change within " + (G $OwnSettle) + ' s after the action.') }
}

# $own: LiteBro's own picture will do when the screen broadcast is not there (not after a button: it takes
# LiteBro off the screen)
function Acted($opt, $steps, [string]$label, [string]$more, $box, [bool]$own) {
    if (-not $opt.ContainsKey('shot')) {
        Run-Steps $steps $box
        Say ($label + ' sent' + (More-Text $more $box))
        return
    }
    $state = Load-State
    $canOwn = $own -and $state.ContainsKey('files')
    if ($opt.ContainsKey('app') -or ($canOwn -and -not $state.ContainsKey('screen'))) {
        Acted-Own $opt $steps $label $more $box $null
        return
    }
    $screen = Need-Server 'screen'
    try {
        [void](Get-Frame $screen $NeverNumber)   # asked before anything is sent: is the broadcast there?
    } catch {
        if (-not ((Is-Refused $_) -and $canOwn)) { throw }
        $first = $_
        $before = $null
        try {
            $before = App-Shot (Need-Server 'files')
        } catch {
            if (-not (FailCode $_)) { throw }
            $before = $null
        }
        if ($null -eq $before) { throw $first }
        Note "note: the screen broadcast does not answer: looking at LiteBro's own screen instead."
        Acted-Own $opt $steps $label $more $box $before
        return
    }
    $seen = Follow $screen $steps $box 0 2.5 0.6 (-1) $null
    $asked = ($null -ne $seen)
    if (-not $asked) { $seen = Observe $screen $steps $box 12.0 2.5 0.6 (-1) $null }   # an older build: its pictures have to be watched for
    Say ($label + ' sent' + (More-Text $more $box))
    if ($null -eq $seen.Picture) {
        Say 'shot none changed=no'
        Note 'note: no picture came: the screen did not change after the action (or the broadcast only began).'
        return
    }
    $path = Shot-Path $opt $null
    $size = Save-Shot $seen.Picture $path
    $changed = 'no'
    if ($seen.New -gt 0) { $changed = 'yes' }
    Say ('shot ' + $path + ' ' + $size.w + 'x' + $size.h + ' changed=' + $changed)
    if ($seen.New -eq 0) {
        $what = 'the one kept from before.'
        if ($asked) { $what = 'the screen as it is.' }
        Note ('note: the screen did not change within 2.5 s after the action; the picture is ' + $what)
    }
}

function New-Box { return @{ Met = $null; Has = $false } }
function New-Steps { return ,(New-Object System.Collections.Generic.List[object]) }

function Cmd-Tap($argv, [bool]$hold) {
    $a = Parse-Args $argv @('system', 'shot', 'app') @('ms', 'out', 'of', 'via') @('opt')
    $word = 'tap'
    $ms = 70.0
    if ($hold) { $word = 'hold'; $ms = 800.0 }
    if ($a.Pos.Count -ne 2) { Fail 1 ($word + ' <x> <y>') }
    $x = Coordinate $a.Pos[0] 'w' $a.Opt
    $y = Coordinate $a.Pos[1] 'h' $a.Opt
    $ms = Opt-Number $a.Opt 'ms' $ms
    $target = Touch-Target $a.Opt
    $extra = Finger-Options $a.Opt
    $steps = New-Steps
    $steps.Add((Finger-Step $target $extra 'down' $x $y))
    $steps.Add(@{ Kind = 'sleep'; Seconds = ($ms / 1000.0) })
    $steps.Add((Finger-Step $target $extra 'up' $x $y))
    $label = $word + ' ' + (F4 $x) + ' ' + (F4 $y)
    if ($a.Opt.ContainsKey('system')) { $label += ' system' }
    Acted $a.Opt $steps $label 'finger' (New-Box) $true
}

function Cmd-Swipe($argv) {
    $a = Parse-Args $argv @('system', 'shot', 'app') @('ms', 'out', 'of', 'via', 'steps') @('opt')
    if ($a.Pos.Count -ne 4) { Fail 1 'swipe <x1> <y1> <x2> <y2>' }
    $x1 = Coordinate $a.Pos[0] 'w' $a.Opt
    $y1 = Coordinate $a.Pos[1] 'h' $a.Opt
    $x2 = Coordinate $a.Pos[2] 'w' $a.Opt
    $y2 = Coordinate $a.Pos[3] 'h' $a.Opt
    $ms = Opt-Number $a.Opt 'ms' 300.0
    $count = [Math]::Max(1, (Opt-Int $a.Opt 'steps' 12))
    $target = Touch-Target $a.Opt
    $extra = Finger-Options $a.Opt
    $steps = New-Steps
    $steps.Add((Finger-Step $target $extra 'down' $x1 $y1))
    for ($i = 1; $i -le $count; $i++) {
        $steps.Add(@{ Kind = 'sleep'; Seconds = ($ms / 1000.0 / $count) })
        $k = $i / [double]$count
        if ($i -eq $count) {
            $steps.Add((Finger-Step $target $extra 'move' $x2 $y2))   # the last step is the end point itself, not a rounding of it
        } else {
            $steps.Add((Finger-Step $target $extra 'move' ($x1 + ($x2 - $x1) * $k) ($y1 + ($y2 - $y1) * $k)))
        }
    }
    $steps.Add((Finger-Step $target $extra 'up' $x2 $y2))
    $label = 'swipe ' + (F4 $x1) + ' ' + (F4 $y1) + ' ' + (F4 $x2) + ' ' + (F4 $y2)
    if ($a.Opt.ContainsKey('system')) { $label += ' system' }
    Acted $a.Opt $steps $label '' (New-Box) $true
}

# The text of a file given to --file, as it is
function Read-Text([string]$file) {
    $full = Local-Path $file
    if (-not [System.IO.File]::Exists($full)) { Fail 1 ('no such file here: ' + $file) }
    return [System.IO.File]::ReadAllText($full, $Utf8)
}

function Cmd-Type($argv) {
    $a = Parse-Args $argv @('shot', 'app', 'enter') @('out', 'file') $null
    $text = ''
    if ($a.Opt.ContainsKey('file') -and $a.Pos.Count -eq 0) { $text = Read-Text $a.Opt['file'] }
    elseif ($a.Pos.Count -gt 0 -and -not $a.Opt.ContainsKey('file')) { $text = ($a.Pos -join ' ') }
    else { Fail 1 'type <text> [--enter], or type --file <text file>' }
    $server = Need-Server 'files'
    $steps = New-Steps
    $steps.Add((Text-Step $server $text))
    $count = 0   # characters as Python counts them: a pair of surrogates is one
    foreach ($c in $text.ToCharArray()) { if (-not [char]::IsLowSurrogate($c)) { $count++ } }
    $label = 'type ' + $count + ' characters'
    if ($a.Opt.ContainsKey('enter')) {
        if ($a.Opt.ContainsKey('shot')) { $steps.Add(@{ Kind = 'sleep'; Seconds = $PaceWait }) }
        $steps.Add((Text-Step $server "`n"))
        $label += ' and enter'
    }
    Acted $a.Opt $steps $label '' (New-Box) $true
}

function Cmd-Key($argv) {
    $a = Parse-Args $argv @('shot', 'app') @('count', 'out') $null
    $keys = @{ backspace = [string][char]8; enter = "`n" }
    if ($a.Pos.Count -ne 1 -or -not $keys.ContainsKey($a.Pos[0]) -or ($a.Pos[0] -cne 'backspace' -and $a.Pos[0] -cne 'enter')) { Fail 1 'key backspace|enter [--count N]' }
    $count = [Math]::Max(1, (Opt-Int $a.Opt 'count' 1))
    $server = Need-Server 'files'
    $steps = New-Steps
    for ($i = 0; $i -lt $count; $i++) {
        if ($i -gt 0 -and $a.Opt.ContainsKey('shot')) { $steps.Add(@{ Kind = 'sleep'; Seconds = $PaceWait }) }
        $steps.Add((Text-Step $server $keys[$a.Pos[0]]))
    }
    Acted $a.Opt $steps ('key ' + $a.Pos[0] + ' x' + $count) '' (New-Box) $true
}

function Cmd-Button($argv) {
    $a = Parse-Args $argv @('yes', 'shot') @('via', 'out') $null
    if ($a.Pos.Count -ne 1 -or ($a.Pos[0] -cne 'home' -and $a.Pos[0] -cne 'side')) { Fail 1 'button home|side --yes' }
    if (-not $a.Opt.ContainsKey('yes')) {
        Fail 1 ('button ' + $a.Pos[0] + ' takes LiteBro off the screen: files, taps and text stop answering until the person opens LiteBro again, and nothing from the computer can bring it back. Add --yes to send it.')
    }
    $target = Touch-Target $a.Opt
    $o = New-Object System.Collections.Specialized.OrderedDictionary
    $o['button'] = $a.Pos[0]
    $steps = New-Steps
    $steps.Add(@{ Kind = 'touch'; Server = $target.Server; Name = $target.Name; Payload = $o })
    Acted $a.Opt $steps ('button ' + $a.Pos[0]) '' (New-Box) $false
}

# A request given as JSON text or as key=value items, as an ordered dictionary
function Request-Of($items, [string]$usage) {
    $o = New-Object System.Collections.Specialized.OrderedDictionary
    if ($items.Count -gt 0 -and $items[0].TrimStart().StartsWith('{')) {
        $text = ($items -join ' ')
        $got = From-Json $text
        if (-not $got.Ok -or -not (Is-Object $got.Value)) { Fail 1 ('not JSON: ' + $text) }
        return $got.Value
    } else {
        Add-Pairs $o $items
    }
    return $o
}

function Cmd-Touch($argv) {
    $a = Parse-Args $argv $null @('via') $null
    $target = Touch-Target $a.Opt
    if ($a.Pos.Count -eq 0) {
        $r = Send-Request $target.Server 'GET' ($target.Server.Base + $target.Name) $null $null 0 $null
        Check-Status $r.Status 'touch' $null
        Say (Text-Of-Bytes $r.Data)
        return
    }
    $payload = Request-Of $a.Pos ''
    [void](Post-Touch $target.Server $target.Name $payload)
    Say 'sent'
}

# --- the page LiteBro shows: test commands of the phone (Remote.swift), no fingers involved ---

# Percent-encodes what an address may not hold as it is; what is already encoded stays
function Quote-Keep([string]$s) {
    $safe = '-._~:/?#[]@!$&''()*+,;=%'
    $b = New-Object System.Text.StringBuilder
    foreach ($byte in $Utf8.GetBytes($s)) {
        $c = [char]$byte
        $plain = ($byte -ge 48 -and $byte -le 57) -or ($byte -ge 65 -and $byte -le 90) -or ($byte -ge 97 -and $byte -le 122)
        if ($plain -or ($byte -lt 128 -and $safe.IndexOf($c) -ge 0)) { [void]$b.Append($c) }
        else { [void]$b.Append('%' + $byte.ToString('X2', $Inv)) }
    }
    return $b.ToString()
}

# An address as iOS reads it: no space and no letter outside ASCII, the scheme in small letters
function Address([string]$url) {
    $clean = $url.Trim()
    $at = $clean.IndexOf('://', [System.StringComparison]::Ordinal)
    $scheme = 'https'
    $rest = $clean
    if ($at -ge 0) {
        $scheme = $clean.Substring(0, $at)
        $rest = $clean.Substring($at + 3)
    }
    return $scheme.ToLowerInvariant() + '://' + (Quote-Keep $rest)
}

# POST _remote/<name> with JSON in and out. Gives @{ Status; Answer } (Answer: an object, whatever came).
function Remote-Call([string]$name, $payload, [double]$timeout) {
    $files = Need-Server 'files'
    if ($null -eq $payload) { $payload = New-Object System.Collections.Specialized.OrderedDictionary }
    $r = Send-Request $files 'POST' ($files.Base + '_remote/' + [System.Uri]::EscapeDataString($name)) ($Utf8.GetBytes((To-Json $payload))) @{ 'Content-Type' = 'application/json' } $timeout $null
    $answer = New-Object System.Collections.Specialized.OrderedDictionary
    $got = From-Json (Text-Of-Bytes $r.Data)
    if ($got.Ok) {
        if (Is-Object $got.Value) { $answer = $got.Value } else { $answer['result'] = $got.Value }
    }
    $status = $r.Status
    if ($status -eq 403) { Fail 3 'the phone refused (403): touch control is not switched on with the screen permission, or the permission ran out' }
    if ($status -eq 405) { Fail 1 'this build of LiteBro has no page commands: update it on the phone (iphone install)' }
    if ($status -eq 404 -and (Field $answer 'error')) { Fail 1 ('the phone does not know the command ' + $name) }
    if ($status -eq 400) {
        $what = Field $answer 'error'
        if (-not $what) { $what = 'value' }
        Fail 1 ('the phone did not take the request: wrong or missing ' + $what)
    }
    if ($status -ne 200 -and $status -ne 409 -and $status -ne 504) { Check-Status $status $name @() }
    return @{ Status = $status; Answer = $answer }
}

function Show-Page($state, [bool]$asJson) {
    if ($asJson) { Say (To-Json $state); return }
    $problem = Field $state 'error'
    if ($problem) { Fail 1 ('LiteBro has no browser to ask: ' + $problem) }
    $url = Field $state 'url'
    if (-not $url) { $url = '(the tiles: no page)' }
    Say ('url      ' + $url)
    Say ('title    ' + [string](Field $state 'title'))
    $loading = 'no'
    if (Field $state 'loading') { $loading = 'yes' }
    Say ('loading  ' + $loading)
    $tabs = '?'
    if (Has-Field $state 'tabs') { $tabs = [string](Field $state 'tabs') }
    Say ('tabs     ' + $tabs)
}

function Cmd-Page($argv) {
    $a = Parse-Args $argv @('json') $null $null
    Show-Page ((Remote-Call 'state' $null 0).Answer) ($a.Opt.ContainsKey('json'))
}

function Cmd-Open($argv) {
    $a = Parse-Args $argv @('new-tab', 'json') @('wait') $null
    if ($a.Pos.Count -ne 1) { Fail 1 'open <address> [--new-tab] [--wait 20]' }
    $wait = Opt-Number $a.Opt 'wait' 20.0
    $o = New-Object System.Collections.Specialized.OrderedDictionary
    $o['url'] = Address $a.Pos[0]
    $o['newTab'] = $a.Opt.ContainsKey('new-tab')
    $state = (Remote-Call 'open' $o 0).Answer
    $deadline = (Now) + $wait
    while ((Field $state 'loading') -and (Now) -lt $deadline) {
        Wait-Seconds 0.5
        $state = (Remote-Call 'state' $null 0).Answer
    }
    Show-Page $state ($a.Opt.ContainsKey('json'))
    if (Field $state 'loading') { Note ('note: the page is still loading after ' + (G $wait) + ' s') }
}

function Cmd-Js($argv) {
    $a = Parse-Args $argv @('json') @('file', 'timeout') $null
    $code = ''
    if ($a.Opt.ContainsKey('file') -and $a.Pos.Count -eq 0) { $code = (Read-Text $a.Opt['file']).Replace("`r`n", "`n").Replace("`r", "`n") }
    elseif ($a.Pos.Count -gt 0 -and -not $a.Opt.ContainsKey('file')) { $code = ($a.Pos -join ' ') }
    else { Fail 1 'js <code>, or js --file <file>: the body of an async function, its return comes back' }
    $seconds = Opt-Number $a.Opt 'timeout' 20.0
    $o = New-Object System.Collections.Specialized.OrderedDictionary
    $o['code'] = $code
    $o['timeout'] = $seconds
    $r = Remote-Call 'js' $o ($seconds + 5)
    if ($r.Status -eq 409) { Fail 1 'no page is open in the current tab (it shows the tiles): open an address first' }
    if ($r.Status -eq 504) { Fail 1 ("the page's code did not finish in " + (G $seconds) + ' s') }
    if (Has-Field $r.Answer 'error') { Fail 1 ("the page's code failed: " + [string](Field $r.Answer 'error')) }
    $result = Field $r.Answer 'result'
    if ($result -is [string] -and -not $a.Opt.ContainsKey('json')) { Say $result } else { Say (To-Json $result) }
}

function Cmd-Click($argv) {
    $a = Parse-Args $argv @('shot', 'app') @('out', 'of') $null
    if ($a.Pos.Count -ne 2) { Fail 1 'click <x> <y>' }
    $x = Coordinate $a.Pos[0] 'w' $a.Opt
    $y = Coordinate $a.Pos[1] 'h' $a.Opt
    $o = New-Object System.Collections.Specialized.OrderedDictionary
    $o['x'] = $x
    $o['y'] = $y
    $steps = New-Steps
    $steps.Add(@{ Kind = 'remote'; Name = 'click'; Payload = $o })
    $box = New-Box
    Acted $a.Opt $steps ('click ' + (F4 $x) + ' ' + (F4 $y)) 'click' $box $true
    if ($box.Has -and -not (Field $box.Met 'control')) {
        Note 'note: there is no button or switch of LiteBro at that point, so nothing was pressed. click does not press what a page draws: use js for a page (document.querySelector(...).click()).'
    }
}

function Cmd-Remote($argv) {
    $a = Parse-Args $argv $null $null $null
    if ($a.Pos.Count -eq 0) { Fail 1 'remote <name> [json | key=value ...]' }
    $rest = New-Object System.Collections.Generic.List[string]
    for ($i = 1; $i -lt $a.Pos.Count; $i++) { $rest.Add($a.Pos[$i]) }
    $r = Remote-Call $a.Pos[0] (Request-Of $rest '') 0
    Say (To-Json $r.Answer)
    if ($r.Status -ne 200) { $script:ExitCode = 1 }
}

# --- the app ---

# The end of LiteBro's log: @{ Rel; Text }
function Read-Log($server, [int]$size) {
    foreach ($rel in $LogPaths) {
        $r = Send-Request $server 'GET' (Server-Path $server $rel) $null @{ 'Range-Last' = $size } 0 $null
        if ($r.Status -eq 200 -or $r.Status -eq 206) { return @{ Rel = $rel; Text = (Text-Of-Bytes $r.Data) } }
        if ($r.Status -ne 404) { Check-Status $r.Status $rel $null }
    }
    Fail 4 ("LiteBro's log is not there (" + ($LogPaths -join ' or ') + ')')
}

function Cmd-Log($argv) {
    $a = Parse-Args $argv @('launch') @('tail') $null
    $lines = Split-Lines ((Read-Log (Need-Server 'files') 262144).Text)
    if ($a.Opt.ContainsKey('launch')) {
        $from = 0
        for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].StartsWith('===== launch', [System.StringComparison]::Ordinal)) { $from = $i } }
        Say (($lines.GetRange($from, $lines.Count - $from)) -join "`n")
        return
    }
    $n = Opt-Int $a.Opt 'tail' 60
    $from = [Math]::Min($lines.Count, [Math]::Max(0, $lines.Count - $n))
    Say (($lines.GetRange($from, $lines.Count - $from)) -join "`n")
}

# `LiteBro 1.9.0 build 45363f3, iOS 16.2.0, iPhone14,5` of the last launch in the log, or $null
function App-Line($server) {
    $text = ''
    try {
        $text = (Read-Log $server 65536).Text
    } catch {
        if (FailCode $_) { return $null }
        throw
    }
    $found = $null
    foreach ($l in (Split-Lines $text)) {
        if ($l.StartsWith('LiteBro ', [System.StringComparison]::Ordinal) -and $l.Contains(' build ')) { $found = $l }
    }
    return $found
}

# What the files address answers: scope, writable, taps
function Probe-Files($server) {
    $r = Send-Request $server 'PROPFIND' $server.Base $null @{ Depth = '1' } 0 $null
    if ($r.Status -eq 404) { return @{ ok = $false; why = 'wrong address: the phone answers 404 (an old secret? access was opened again)' } }
    if ($r.Status -ne 207) { return @{ ok = $false; why = ('not the files address: it answers ' + $r.Status + ' to a folder listing') } }
    $listing = Text-Of-Bytes $r.Data
    $whole = $listing.Contains('Media') -and $listing.Contains('Library')
    $r = Send-Request $server 'PROPPATCH' $server.Base $null $null 0 $null
    $writable = ($r.Status -eq 207)
    $r = Send-Request $server 'GET' ($server.Base + '_touch') $null $null 0 $null
    $taps = ($r.Status -eq 200)
    $hid = ''
    if ($taps) { $hid = Text-Of-Bytes $r.Data }
    return @{ ok = $true; whole = $whole; writable = $writable; taps = $taps; hid = $hid }
}

function Probe-Screen($server) {
    $r = Send-Request $server 'GET' $server.Base $null $null 0 $null
    if ($r.Status -eq 404) { return @{ ok = $false; why = 'wrong address: the phone answers 404 (an old secret? the screen was allowed again)' } }
    if ($r.Status -ne 200 -or -not (Text-Of-Bytes $r.Data).Contains('stream')) { return @{ ok = $false; why = 'not the screen address' } }
    return @{ ok = $true }
}

# Which of the two addresses this is, by asking it: @{ Kind = 'files' | 'screen' | $null; Why }
function Kind-Of([string]$url) {
    $server = New-Server $url 5.0
    $guess = 'files'
    if ($server.Port -eq 8081) { $guess = 'screen' }
    $r = $null
    try {
        $r = Send-Request $server 'OPTIONS' $server.Base $null $null 0 $null
    } catch {
        # Nothing answers: the screen address is on 8081 and is silent until the broadcast runs
        if (FailCode $_) { return @{ Kind = $guess; Why = (FailText $_) } }
        throw
    }
    if ($r.Headers['DAV']) { return @{ Kind = 'files'; Why = $null } }
    if ($r.Status -eq 200 -and (Text-Of-Bytes $r.Data).Contains('stream')) { return @{ Kind = 'screen'; Why = $null } }
    if ($r.Status -eq 404) { return @{ Kind = $guess; Why = 'wrong address: the phone answers 404 (an old secret?)' } }
    return @{ Kind = $null; Why = ('neither the files nor the screen address of LiteBro (it answers ' + $r.Status + ')') }
}

# The lines of "status"; gives the exit code
function Report($state, [bool]$asJson) {
    $info = New-Object System.Collections.Specialized.OrderedDictionary
    $code = 0
    $lines = New-Object System.Collections.Generic.List[string]
    if ($state.ContainsKey('files')) {
        $server = New-Server $state['files'] 5.0
        $p = $null
        try {
            $p = Probe-Files $server
        } catch {
            if (-not (FailCode $_)) { throw }
            $p = @{ ok = $false; why = (FailText $_) }
            $code = 2
        }
        $o = New-Object System.Collections.Specialized.OrderedDictionary
        $o['ok'] = [bool]$p.ok
        if ($p.ok) {
            $o['whole'] = [bool]$p.whole
            $o['writable'] = [bool]$p.writable
            $o['taps'] = [bool]$p.taps
            $o['hid'] = [string]$p.hid
        } else {
            $o['why'] = [string]$p.why
        }
        $o['url'] = $server.Url
        $info['files'] = $o
        if ($p.ok) {
            $scope = 'LiteBro folder only'
            if ($p.whole) { $scope = 'whole phone (/var/mobile)' }
            $mode = 'read-only'
            if ($p.writable) { $mode = 'writable' }
            $lines.Add('files   ok, ' + $scope + ', ' + $mode)
            $taps = 'off (touch control is not switched on, or the screen permission ran out)'
            if ($p.taps) { $taps = 'allowed' }
            $lines.Add('taps    ' + $taps)
            $app = App-Line $server
            if ($app) {
                $info['app'] = $app
                $lines.Add('app     ' + $app)
            }
        } else {
            $lines.Add('files   ' + $p.why)
            if ($code -eq 0) { $code = 4 }
        }
    } else {
        $lines.Add('files   not set')
    }
    if ($state.ContainsKey('screen')) {
        $server = New-Server $state['screen'] 5.0
        $p = $null
        try {
            $p = Probe-Screen $server
        } catch {
            if (-not (FailCode $_)) { throw }
            $p = @{ ok = $false; why = (FailText $_) }
        }
        $o = New-Object System.Collections.Specialized.OrderedDictionary
        $o['ok'] = [bool]$p.ok
        if (-not $p.ok) { $o['why'] = [string]$p.why }
        $o['url'] = $server.Url
        $info['screen'] = $o
        if ($p.ok) { $lines.Add('screen  ok, the broadcast runs') } else { $lines.Add('screen  ' + $p.why) }
    } else {
        $lines.Add('screen  not set')
    }
    if ($asJson) { Say (To-Json $info) } else { foreach ($l in $lines) { Say $l } }
    return $code
}

function Full-Address([string]$url) {
    if ($url.Contains('://')) { return $url }
    return 'http://' + $url
}

function Cmd-Connect($argv) {
    $a = Parse-Args $argv $null @('files', 'screen') $null
    if ($a.Pos.Count -gt 2 -or ($a.Pos.Count -eq 0 -and $a.Opt.Count -eq 0)) {
        Fail 1 'connect <address> [<address>], or connect --files <address> --screen <address>: the addresses the phone shows'
    }
    $state = Load-State
    foreach ($kind in @('files', 'screen')) {
        if ($a.Opt.ContainsKey($kind)) { $state[$kind] = (New-Server (Full-Address $a.Opt[$kind]) 0).Url }
    }
    foreach ($url in $a.Pos) {
        $k = Kind-Of (Full-Address $url)
        if ($null -eq $k.Kind) { Fail 1 ($url + ': ' + $k.Why) }
        $state[$k.Kind] = (New-Server (Full-Address $url) 0).Url
    }
    $state.Remove('shot')
    Save-State $state
    $code = Report $state $false
    Say ('saved   ' + $StateFile)
    $script:ExitCode = $code
}

function Cmd-Status($argv) {
    $a = Parse-Args $argv @('json') $null $null
    $state = Load-State
    if (-not $state.ContainsKey('files') -and -not $state.ContainsKey('screen')) { Fail 1 'no address yet: iphone connect <address from the phone>' }
    $script:ExitCode = Report $state ($a.Opt.ContainsKey('json'))
}

function Cmd-Forget($argv) {
    $a = Parse-Args $argv $null $null $null
    try { if ([System.IO.File]::Exists($StateFile)) { [System.IO.File]::Delete($StateFile) } } catch { }
    Say 'forgotten'
}

function Cmd-Wait($argv) {
    $a = Parse-Args $argv $null @('seconds') $null
    $kind = 'files'
    if ($a.Pos.Count -gt 0) { $kind = $a.Pos[0] }
    if ($kind -cne 'files' -and $kind -cne 'screen') { Fail 1 'wait [files|screen] [--seconds 120]' }
    $seconds = Opt-Number $a.Opt 'seconds' 120.0
    $deadline = (Now) + $seconds
    while ($true) {
        $server = Need-Server $kind
        $server.Timeout = [Math]::Min(3.0, $AnswerWait)
        $why = ''
        try {
            $p = $null
            if ($kind -ceq 'files') { $p = Probe-Files $server } else { $p = Probe-Screen $server }
            if ($p.ok) { Say ($kind + ' answers'); return }
            $why = $p.why
        } catch {
            if (-not (FailCode $_)) { throw }
            $why = FailText $_
        }
        if ((Now) -ge $deadline) { Fail 2 ($kind + ' did not answer in ' + (G $seconds) + ' s: ' + $why) }
        Wait-Seconds 2
    }
}

# LiteBro's own folder in the Files app of the phone, as a path of the shared root
function LiteBro-Documents($files) {
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($e in (Propfind $files '' 1).Inside) { if ($e.dir) { $names.Add($e.name) } }
    if (-not $names.Contains('Media') -or -not $names.Contains('Containers')) { return '' }   # the build that shares only that folder: the root is it
    $text = ''
    try {
        $text = (Read-Log $files 65536).Text
    } catch {
        if (-not (FailCode $_)) { throw }
    }
    $appHome = $null
    $mark = 'data /var/mobile/'
    foreach ($l in (Split-Lines $text)) {
        if ($l.StartsWith($mark, [System.StringComparison]::Ordinal)) { $appHome = $l.Substring($mark.Length).Trim().Trim('/') }
    }
    if (-not $appHome) {
        $base = 'Containers/Data/Application'
        foreach ($e in (Propfind $files $base 1).Inside) {
            if ((Container-Id $files (PJoin $base $e.name)) -ceq 'io.github.ronanime-arch.LiteBro') {
                $appHome = PJoin $base $e.name
                break
            }
        }
    }
    if (-not $appHome) { Fail 4 "LiteBro's own folder was not found on the phone" }
    return $appHome + '/Documents'
}

function Cmd-Install($argv) {
    $a = Parse-Args $argv @('latest') $null $null
    if ($a.Pos.Count -gt 1) { Fail 1 'install (the released build), or install <file.tipa> (a file for TrollStore, opened by hand)' }
    $files = Need-Server 'files'
    if ($a.Pos.Count -eq 0) {
        # The phone installs by itself only LiteBro's own released build, and only this address of it
        $o = New-Object System.Collections.Specialized.OrderedDictionary
        $o['url'] = $OwnBuild
        $r = Send-Request $files 'POST' ($files.Base + '_install') ($Utf8.GetBytes((To-Json $o))) @{ 'Content-Type' = 'application/json' } 0 $null
        if ($r.Status -eq 403) { Fail 3 'the phone refused the install (403): it needs touch control switched on with the screen permission' }
        if ($r.Status -eq 409) {
            Fail 3 "the phone did not start the install (409): its command would open the system Magnifier, not TrollStore. Switch on URL Scheme in TrollStore's settings on the phone, then try again"
        }
        Check-Status $r.Status 'install' @(202)
        Say ('asked the phone to install ' + $OwnBuild)
        Say 'TrollStore opens on the phone and installs it (the person confirms there if it asks). LiteBro closes: the person opens it again and opens access again, then connect with the new addresses.'
        return
    }
    $source = $a.Pos[0]
    $full = Local-Path $source
    if (-not [System.IO.File]::Exists($full)) { Fail 1 ('no such file here: ' + $source) }
    # Any other build goes to the phone as a file; the person opens it with TrollStore
    $name = [System.IO.Path]::GetFileName($full)
    $rel = PJoin (LiteBro-Documents $files) $name
    $size = Upload $files $full $rel
    Say ('uploaded ' + $rel + ' (' + $size + ' bytes)')
    Say ('The phone installs by itself only the released build (iphone install). For this file the person opens the Files app on the phone, On My iPhone, LiteBro, ' + $name + ', Share, TrollStore, Install.')
}

function Main($argv) {
    $argv = @($argv)
    if ($argv.Count -eq 0 -or @('help', '--help', '-h') -ccontains $argv[0]) { Say $HelpText; return }
    $rest = New-Object System.Collections.Generic.List[string]
    for ($i = 1; $i -lt $argv.Count; $i++) { $rest.Add([string]$argv[$i]) }
    $rest = $rest.ToArray()
    switch -CaseSensitive ([string]$argv[0]) {
        'connect' { Cmd-Connect $rest }
        'status' { Cmd-Status $rest }
        'forget' { Cmd-Forget $rest }
        'wait' { Cmd-Wait $rest }
        'ls' { Cmd-Ls $rest }
        'tree' { Cmd-Tree $rest }
        'find' { Cmd-Find $rest }
        'du' { Cmd-Du $rest }
        'cat' { Cmd-Cat $rest }
        'plist' { Cmd-Plist $rest }
        'apps' { Cmd-Apps $rest }
        'get' { Cmd-Get $rest }
        'put' { Cmd-Put $rest }
        'mkdir' { Cmd-Mkdir $rest }
        'rm' { Cmd-Rm $rest }
        'mv' { Cmd-Move $rest 'MOVE' 'mv' }
        'cp' { Cmd-Move $rest 'COPY' 'cp' }
        'shot' { Cmd-Shot $rest }
        'watch' { Cmd-Watch $rest }
        'page' { Cmd-Page $rest }
        'open' { Cmd-Open $rest }
        'js' { Cmd-Js $rest }
        'click' { Cmd-Click $rest }
        'remote' { Cmd-Remote $rest }
        'tap' { Cmd-Tap $rest $false }
        'hold' { Cmd-Tap $rest $true }
        'swipe' { Cmd-Swipe $rest }
        'type' { Cmd-Type $rest }
        'key' { Cmd-Key $rest }
        'button' { Cmd-Button $rest }
        'touch' { Cmd-Touch $rest }
        'log' { Cmd-Log $rest }
        'install' { Cmd-Install $rest }
        default {
            Note ('iphone: unknown command ' + $argv[0] + ' (see: iphone help)')
            $script:ExitCode = 1
        }
    }
}

$ExitCode = 0
$GivenWords = New-Object System.Collections.Generic.List[string]
foreach ($given in $args) { $GivenWords.Add([string]$given) }
try {
    $null = Main ($GivenWords.ToArray())
} catch {
    Note ('iphone: ' + (FailText $_))
    $ExitCode = FailCode $_
    if ($ExitCode -eq 0) { $ExitCode = 1 }
}
$StdOut.Flush()
$StdErr.Flush()
exit $ExitCode
