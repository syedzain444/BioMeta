using System.Text.RegularExpressions;
using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Utilities;

public static partial class EmployeeImportValidator
{
    private const int MinIdDigits = 1;
    private const int MaxIdDigits = 20;

    public static EmployeeImportValidationResult Validate(IReadOnlyList<EmployeeImportRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var valid = 0;
        foreach (var row in rows)
        {
            row.Errors.Clear();
            row.Warnings.Clear();
            row.EmployeeId = NicIdNormalizer.Normalize(row.EmployeeId);
            if (string.IsNullOrWhiteSpace(row.EmployeeId))
            {
                row.Errors.Add("Employee ID is required.");
                continue;
            }

            if (row.EmployeeId.Length < MinIdDigits || row.EmployeeId.Length > MaxIdDigits)
                row.Warnings.Add($"ID length {row.EmployeeId.Length} — expected {MinIdDigits}–{MaxIdDigits} digits.");

            if (!DigitsOnly().IsMatch(row.EmployeeId))
                row.Errors.Add("Employee ID must contain digits only (dashes are stripped).");

            if (!seen.Add(row.EmployeeId))
                row.Errors.Add("Duplicate employee ID in this import.");

            if (string.IsNullOrWhiteSpace(row.Name))
                row.Warnings.Add("Name is empty — device may show a blank name.");

            if (!string.IsNullOrWhiteSpace(row.FaceDataBase64) && row.FaceDataBase64!.Length < 32)
                row.Warnings.Add("face_data looks too short — upload may fail on device.");

            if (row.IsValid) valid++;
        }

        return new EmployeeImportValidationResult
        {
            TotalRows = rows.Count,
            ValidRows = valid,
            InvalidRows = rows.Count - valid,
            Rows = rows,
        };
    }

    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex DigitsOnly();
}