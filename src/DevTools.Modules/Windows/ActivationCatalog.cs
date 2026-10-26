namespace DevTools.Modules.Windows;

public sealed record WindowsEdition(string Id, int Build, string ProductName, string InstallationType = "Client", int Revision = 0)
{
    public string Display => InstallationType.Contains("Server", StringComparison.OrdinalIgnoreCase) ? ProductName : $"Windows {(Build >= 22000 ? "11" : "10")} {ActivationCatalog.Names.GetValueOrDefault(Id, Id)}";
}
public static class ActivationCatalog
{
    // Microsoft GVLKs: https://learn.microsoft.com/windows-server/get-started/kms-client-activation-keys
    public static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Professional"]="W269N-WFGWX-YVC9B-4J6C9-T83GX", ["ProfessionalN"]="MH37W-N47XK-V7XM9-C7227-GCQG9",
        ["ProfessionalWorkstation"]="NRG8B-VKK3Q-CXVCJ-9G2XF-6Q84J", ["ProfessionalWorkstationN"]="9FNHH-K3HBT-3W4TD-6383H-6XYWF",
        ["ProfessionalEducation"]="6TP4R-GNPTD-KYYHQ-7B7DP-J447Y", ["ProfessionalEducationN"]="YVWGF-BXNMC-HTQYQ-CPQ99-66QFC",
        ["Education"]="NW6C2-QMPVW-D7KKK-3GKT6-VCFB2", ["EducationN"]="2WH4N-8QGBV-H22JP-CT43Q-MDWWJ",
        ["Enterprise"]="NPPR9-FWDCX-D2C8J-H872K-2YT43", ["EnterpriseN"]="DPH2V-TTNVB-4X9Q3-TJR4H-KHJW4",
        ["EnterpriseG"]="YYVX9-NTFWV-6MDM3-9PT4T-4M68B", ["EnterpriseGN"]="44RPN-FTY23-9VTTB-MP9BX-T84FV"
    };
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Professional"]="Pro", ["ProfessionalN"]="Pro N", ["ProfessionalWorkstation"]="Pro for Workstations", ["ProfessionalWorkstationN"]="Pro for Workstations N",
        ["ProfessionalEducation"]="Pro Education", ["ProfessionalEducationN"]="Pro Education N", ["Education"]="Education", ["EducationN"]="Education N",
        ["Enterprise"]="Enterprise", ["EnterpriseN"]="Enterprise N", ["EnterpriseG"]="Enterprise G", ["EnterpriseGN"]="Enterprise G N",
        ["EnterpriseS"]="Enterprise LTSC", ["EnterpriseSN"]="Enterprise N LTSC", ["IoTEnterpriseS"]="IoT Enterprise LTSC", ["Core"]="Home", ["CoreN"]="Home N", ["CoreSingleLanguage"]="Home Single Language"
    };
    public static string? Match(WindowsEdition edition)
    {
        if (edition.InstallationType.Contains("Server", StringComparison.OrdinalIgnoreCase) || edition.Build < 10240) return null;
        if (Keys.TryGetValue(edition.Id, out var key)) return key;
        if (edition.Id.Equals("EnterpriseS", StringComparison.OrdinalIgnoreCase)) return edition.Build < 14393 ? "WNMTR-4C88C-JK8YV-HQ7T2-76DF9" : edition.Build < 17763 ? "DCPHK-NFMTC-H88MJ-PFHPY-QJ4BJ" : "M7XTQ-FN8P6-TTKYV-9D4CC-J462D";
        if (edition.Id.Equals("EnterpriseSN", StringComparison.OrdinalIgnoreCase)) return edition.Build < 14393 ? "2F77B-TNFGY-69QQF-B8YKP-D69TJ" : edition.Build < 17763 ? "QFFDN-GRT3P-VKWWX-X7T3R-8B639" : "92NFX-8DJQP-P6BBQ-THF9C-7CG2H";
        if (edition.Id.Equals("IoTEnterpriseS", StringComparison.OrdinalIgnoreCase) && (edition.Build > 19044 || edition.Build == 19044 && edition.Revision >= 2905)) return "KBN8V-HFGQ4-MGXVD-347P6-PDQGT";
        return null;
    }
}
