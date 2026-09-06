using System.IO;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexo.Support;

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Login { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Photo { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool Admin { get; set; }
    public bool Diagnose { get; set; } = true;
    public bool Repair { get; set; }
    public bool Tickets { get; set; } = true;
    public bool Active { get; set; } = true;
    public bool MustChangePassword { get; set; }
}
public sealed class Entry
{
    public string Role { get; set; } = "assistant";
    public string Text { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
}
public sealed class AgentMessage
{
    public string role { get; set; } = "user";
    public string content { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? tool_calls { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? tool_name { get; set; }
}
public sealed class Conversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string Title { get; set; } = "Novo atendimento";
    public string Status { get; set; } = "Em andamento";
    public DateTime Created { get; set; } = DateTime.Now;
    public List<Entry> Entries { get; set; } = [];
    public List<AgentMessage> AgentHistory { get; set; } = [];
    public string? PendingRestartBoot { get; set; }
    public string? PendingRestartReason { get; set; }
}
public sealed class Ticket
{
    public string Id { get; set; } = "NX-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    public string ConversationId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Report { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
}
public sealed class Audit
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
}
public sealed class Database
{
    public List<Account> Accounts { get; set; } = [];
    public List<Conversation> Conversations { get; set; } = [];
    public List<Ticket> Tickets { get; set; } = [];
    public List<Audit> Audit { get; set; } = [];
    public string Model { get; set; } = "";
    public bool AiEnabled { get; set; }
    public int AgentVersion { get; set; }
}
public static class Passwords
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
    }
    public static bool Verify(string password, string encoded)
    {
        try
        {
            var p = encoded.Split(':');
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[0]), 210000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(p[1]));
        }
        catch { return false; }
    }
    public static void Validate(string password)
    {
        if (password.Length < 8 || password.Length > 128) throw new InvalidOperationException("Use uma senha de 8 a 128 caracteres.");
    }
}
public sealed class Store
{
    public Database Data { get; }
    public string Root { get; }
    private readonly string path;
    private readonly Dictionary<string, (int Attempts, DateTime Until)> failures = new(StringComparer.OrdinalIgnoreCase);
    public Store(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexoSupport");
        Directory.CreateDirectory(Root);
        path = Path.Combine(Root, "data.json");
        if (File.Exists(path)) Data = JsonSerializer.Deserialize<Database>(File.ReadAllText(path)) ?? throw new IOException("Base de dados inválida.");
        else
        {
            Data = new Database();
            Data.Accounts.Add(new Account { Login = "Guisbz", Name = "Guilherme", PasswordHash = Passwords.Hash("1234"), Admin = true, Repair = true, MustChangePassword = true });
            Save();
        }
        if (Data.AgentVersion < 2)
        {
            Data.Model = AgentService.DefaultModel;
            Data.AiEnabled = true;
            Data.AgentVersion = 2;
            Save();
        }
    }
    public void Save()
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
    public void Log(Account actor, string action)
    {
        Data.Audit.Add(new Audit { Actor = actor.Login, Action = action });
        Save();
    }
    public Account Login(string login, string password)
    {
        login = login.Trim();
        if (failures.TryGetValue(login, out var f) && f.Until > DateTime.UtcNow) throw new InvalidOperationException("Muitas tentativas. Aguarde 1 minuto.");
        var user = Data.Accounts.FirstOrDefault(a => a.Login.Equals(login, StringComparison.OrdinalIgnoreCase) && a.Active);
        if (user == null || !Passwords.Verify(password, user.PasswordHash))
        {
            var attempts = f.Until != default && f.Until <= DateTime.UtcNow ? 1 : f.Attempts + 1;
            failures[login] = (attempts, attempts >= 5 ? DateTime.UtcNow.AddMinutes(1) : default);
            throw new InvalidOperationException("Usuário ou senha incorretos.");
        }
        failures.Remove(login);
        Log(user, "Login realizado");
        return user;
    }
    public void Require(Account actor, Func<Account, bool> permission)
    {
        var current = Data.Accounts.SingleOrDefault(a => a.Id == actor.Id);
        if (current == null || !current.Active || !permission(current)) throw new InvalidOperationException("Sua conta não tem permissão para esta ação.");
    }
    public void SaveAccount(Account actor, Account edited, string? newPassword)
    {
        Require(actor, a => a.Admin);
        if (!Regex.IsMatch(edited.Login, @"^[a-zA-Z0-9._-]{3,40}$")) throw new InvalidOperationException("Login: use de 3 a 40 letras, números, ponto, hífen ou sublinhado.");
        if (string.IsNullOrWhiteSpace(edited.Name)) throw new InvalidOperationException("Informe o nome.");
        ValidateEmail(edited.Email);
        if (Data.Accounts.Any(a => a.Id != edited.Id && a.Login.Equals(edited.Login, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Este login já existe.");
        var old = Data.Accounts.SingleOrDefault(a => a.Id == edited.Id);
        if (old is { Active: true, Admin: true } && (!edited.Active || !edited.Admin) && Data.Accounts.Count(a => a.Active && a.Admin) <= 1) throw new InvalidOperationException("Mantenha pelo menos um administrador ativo.");
        if (edited.Id == actor.Id && (!edited.Active || !edited.Admin)) throw new InvalidOperationException("Peça a outro administrador para alterar seu próprio acesso.");
        if (old == null && string.IsNullOrEmpty(newPassword)) throw new InvalidOperationException("Defina a senha inicial.");
        if (!string.IsNullOrEmpty(newPassword)) { Passwords.Validate(newPassword); edited.PasswordHash = Passwords.Hash(newPassword); edited.MustChangePassword = true; }
        else if (old != null) edited.PasswordHash = old.PasswordHash;
        if (old != null) Data.Accounts.Remove(old);
        Data.Accounts.Add(edited);
        Log(actor, "Conta e permissões atualizadas: " + edited.Login);
    }
    public void UpdateProfile(Account actor, string name, string email, string photo, string currentPassword, string newPassword)
    {
        Require(actor, _ => true);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Informe seu nome.");
        ValidateEmail(email);
        if (newPassword.Length > 0)
        {
            if (!Passwords.Verify(currentPassword, actor.PasswordHash)) throw new InvalidOperationException("A senha atual está incorreta.");
            Passwords.Validate(newPassword);
            actor.PasswordHash = Passwords.Hash(newPassword);
            actor.MustChangePassword = false;
        }
        actor.Name = name.Trim(); actor.Email = email.Trim(); actor.Photo = photo;
        Log(actor, "Perfil atualizado");
    }
    public static void ValidateEmail(string email)
    {
        if (email.Length > 0 && (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)) throw new InvalidOperationException("Informe um e-mail válido.");
    }
}
