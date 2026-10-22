function Read-MsiRows($Database, [string]$Sql) {
    $view = $Database.OpenView($Sql)
    try {
        $null = $view.Execute()
        while ($record = $view.Fetch()) {
            try {
                $values = @(for ($index=1; $index -le $record.FieldCount(); $index++) { $record.StringData($index) })
                ,$values
            } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        }
    } finally { $null = $view.Close(); $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
}

function Read-MsiProperties($Database) {
    $properties = @{}
    foreach ($row in (Read-MsiRows $Database 'SELECT `Property`, `Value` FROM `Property`')) { $properties[$row[0]] = $row[1] }
    return $properties
}

function Invoke-MsiSql($Database, [string]$Sql) {
    $view = $Database.OpenView($Sql)
    try { $null = $view.Execute() }
    finally { $null = $view.Close(); $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
}
