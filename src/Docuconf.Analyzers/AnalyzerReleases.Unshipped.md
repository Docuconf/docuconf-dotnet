; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
DOCUCONF001 | Docuconf | Error | Every input needs a description
DOCUCONF002 | Docuconf | Error | A secret cannot have a default
DOCUCONF003 | Docuconf | Error | The constraint does not fit the property's type
DOCUCONF004 | Docuconf | Error | The default violates the property's constraints
DOCUCONF005 | Docuconf | Error | Environment variable names are UPPER_SNAKE_CASE
DOCUCONF006 | Docuconf | Error | The service name must be a DNS label
DOCUCONF007 | Docuconf | Error | Patterns must be RE2
DOCUCONF008 | Docuconf | Error | A record's ToString prints secrets
DOCUCONF009 | Docuconf | Error | The contract cannot describe this property
DOCUCONF010 | Docuconf | Error | A file input needs its own property type
