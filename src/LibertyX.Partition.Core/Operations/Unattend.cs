using System.Security;
using System.Text;

namespace LibertyX.Partition.Core.Operations;

/// <summary>As opções do Windows 11 que o pendrive de instalação aplica sozinho (como o Rufus faz).</summary>
public sealed record WindowsSetupOptions(bool BypassRequirements, bool LocalAccount, string? UserName, bool SkipPrivacy, bool NoAutoEncryption)
{
    public bool Any => BypassRequirements || LocalAccount || !string.IsNullOrWhiteSpace(UserName) || SkipPrivacy || NoAutoEncryption;

    public string Summary => string.Join("; ", new[]
    {
        BypassRequirements ? "sem exigência de TPM 2.0, Secure Boot e 4 GB de RAM" : null,
        LocalAccount ? "sem exigência de conta Microsoft/internet" : null,
        string.IsNullOrWhiteSpace(UserName) ? null : $"conta local “{UserName}”",
        SkipPrivacy ? "sem as perguntas de privacidade" : null,
        NoAutoEncryption ? "sem criptografia automática do disco" : null,
    }.Where(x => x != null));

    /// <summary>Nome válido para conta local do Windows (nulo = ok).</summary>
    public static string? ValidateUser(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Length > 20) return "O nome da conta tem no máximo 20 caracteres.";
        if (name.IndexOfAny("/\\[]:;|=,+*?<>\"@".ToCharArray()) >= 0 || name.Trim('.', ' ').Length == 0) return "O nome da conta não pode ter / \\ [ ] : ; | = , + * ? < > \" @.";
        return null;
    }
}

/// <summary>
/// Gera o autounattend.xml que o instalador do Windows lê sozinho da raiz do pendrive.
/// - windowsPE: grava as chaves LabConfig (BypassTPMCheck, BypassSecureBootCheck, BypassRAMCheck)
///   antes da checagem de hardware — o mesmo que o Rufus faz.
/// - specialize: BypassNRO (deixa seguir sem internet/conta Microsoft) e PreventDeviceEncryption.
/// - oobeSystem: esconde a tela de conta online, pula as perguntas de privacidade e cria a conta
///   local (sem senha; o Windows pede uma senha nova no primeiro login).
/// O resto da instalação (edição, disco, idioma) continua sendo perguntado normalmente.
/// </summary>
public static class Unattend
{
    private static readonly string[] Archs = ["amd64", "arm64"];

    public static string Build(WindowsSetupOptions o)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<!-- Gerado pelo LibertyX Partition -->");
        sb.AppendLine("<unattend xmlns=\"urn:schemas-microsoft-com:unattend\" xmlns:wcm=\"http://schemas.microsoft.com/WMIConfig/2002/State\">");

        if (o.BypassRequirements)
        {
            sb.AppendLine("  <settings pass=\"windowsPE\">");
            foreach (var a in Archs)
            {
                Component(sb, "Microsoft-Windows-Setup", a);
                sb.AppendLine("      <RunSynchronous>");
                int n = 1;
                foreach (var k in new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck" })
                    Run(sb, "RunSynchronousCommand", n++, $"reg add HKLM\\SYSTEM\\Setup\\LabConfig /v {k} /t REG_DWORD /d 1 /f", "Path");
                sb.AppendLine("      </RunSynchronous>");
                sb.AppendLine("    </component>");
            }
            sb.AppendLine("  </settings>");
        }

        if (o.LocalAccount || o.NoAutoEncryption)
        {
            sb.AppendLine("  <settings pass=\"specialize\">");
            foreach (var a in Archs)
            {
                Component(sb, "Microsoft-Windows-Deployment", a);
                sb.AppendLine("      <RunSynchronous>");
                int n = 1;
                if (o.LocalAccount) Run(sb, "RunSynchronousCommand", n++, "reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE /v BypassNRO /t REG_DWORD /d 1 /f", "Path");
                if (o.NoAutoEncryption) Run(sb, "RunSynchronousCommand", n++, "reg add HKLM\\SYSTEM\\CurrentControlSet\\Control\\BitLocker /v PreventDeviceEncryption /t REG_DWORD /d 1 /f", "Path");
                sb.AppendLine("      </RunSynchronous>");
                sb.AppendLine("    </component>");
            }
            sb.AppendLine("  </settings>");
        }

        bool user = !string.IsNullOrWhiteSpace(o.UserName);
        if (o.LocalAccount || o.SkipPrivacy || user)
        {
            sb.AppendLine("  <settings pass=\"oobeSystem\">");
            foreach (var a in Archs)
            {
                Component(sb, "Microsoft-Windows-Shell-Setup", a);
                sb.AppendLine("      <OOBE>");
                if (o.LocalAccount || user) sb.AppendLine("        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>");
                if (o.SkipPrivacy) sb.AppendLine("        <ProtectYourPC>3</ProtectYourPC>");
                sb.AppendLine("      </OOBE>");
                if (user)
                {
                    string nm = SecurityElement.Escape(o.UserName!.Trim());
                    sb.AppendLine("      <UserAccounts>");
                    sb.AppendLine("        <LocalAccounts>");
                    sb.AppendLine("          <LocalAccount wcm:action=\"add\">");
                    sb.AppendLine($"            <Name>{nm}</Name>");
                    sb.AppendLine($"            <DisplayName>{nm}</DisplayName>");
                    sb.AppendLine("            <Group>Administrators</Group>");
                    sb.AppendLine("            <Password><Value></Value><PlainText>true</PlainText></Password>");
                    sb.AppendLine("          </LocalAccount>");
                    sb.AppendLine("        </LocalAccounts>");
                    sb.AppendLine("      </UserAccounts>");
                    // conta criada sem senha: o primeiro login já pede para definir uma
                    sb.AppendLine("      <FirstLogonCommands>");
                    Run(sb, "SynchronousCommand", 1, $"net user &quot;{nm}&quot; /logonpasswordchg:yes", "CommandLine", escape: false);
                    Run(sb, "SynchronousCommand", 2, "net accounts /maxpwage:unlimited", "CommandLine");
                    sb.AppendLine("      </FirstLogonCommands>");
                }
                sb.AppendLine("    </component>");
            }
            sb.AppendLine("  </settings>");
        }
        sb.AppendLine("</unattend>");
        return sb.ToString();
    }

    private static void Component(StringBuilder sb, string name, string arch) =>
        sb.AppendLine($"    <component name=\"{name}\" processorArchitecture=\"{arch}\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\">");

    private static void Run(StringBuilder sb, string tag, int order, string cmd, string field, bool escape = true)
    {
        sb.AppendLine($"        <{tag} wcm:action=\"add\">");
        sb.AppendLine($"          <Order>{order}</Order>");
        sb.AppendLine($"          <{field}>{(escape ? SecurityElement.Escape(cmd) : cmd)}</{field}>");
        sb.AppendLine($"        </{tag}>");
    }
}
