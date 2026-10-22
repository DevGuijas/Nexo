using System.IO;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
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
    public string Presentation { get; set; } = "";
    public long? DurationMs { get; set; }
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
public sealed record SupportMetrics(int Open, int Resolved, int Escalated, double? ResolutionRate, double? EscalationRate, double? AverageClosureMinutes)
{
    public int Closed => Resolved + Escalated;
    public static SupportMetrics Calculate(Database data)
    {
        var resolved = data.Conversations.Where(c => c.Status == "Resolvido").ToArray();
        var escalated = data.Conversations.Where(c => c.Status == "Encaminhamento preparado").ToArray();
        var open = data.Conversations.Count(c => c.Status != "Resolvido" && c.Status != "Encaminhamento preparado");
        var closed = resolved.Length + escalated.Length;
        if (closed == 0) return new SupportMetrics(open, 0, 0, null, null, null);
        var durations = resolved.Select(c => (c.Entries.LastOrDefault()?.Time ?? c.Created) - c.Created)
            .Concat(escalated.Select(c => (data.Tickets.Where(t => t.ConversationId == c.Id).OrderByDescending(t => t.Created).FirstOrDefault()?.Created ?? c.Entries.LastOrDefault()?.Time ?? c.Created) - c.Created))
            .Select(duration => Math.Max(0, duration.TotalMinutes)).ToArray();
        return new SupportMetrics(open, resolved.Length, escalated.Length,
            (double)resolved.Length / closed, (double)escalated.Length / closed, durations.Average());
    }
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
    private readonly bool directAccess;
    private readonly Dictionary<string, (int Attempts, DateTime Until)> failures = new(StringComparer.OrdinalIgnoreCase);
    public Store(string? root = null, bool directAccess = false)
    {
        this.directAccess = directAccess;
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexoSupport");
        Directory.CreateDirectory(Root);
        path = Path.Combine(Root, "data.json");
        if (File.Exists(path)) Data = JsonSerializer.Deserialize<Database>(File.ReadAllText(path)) ?? throw new IOException("Base de dados inválida.");
        else
        {
            Data = new Database();
            if (!directAccess) Data.Accounts.Add(new Account { Login = "Guisbz", Name = "Guilherme", PasswordHash = Passwords.Hash("1234"), Admin = true, Repair = true, MustChangePassword = true });
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
    public Account GetLocalSession()
    {
        if (!directAccess) throw new InvalidOperationException("A sessão direta não está habilitada.");
        var session = CreateLocalSession();
        var conversations = Data.Conversations.Where(item => item.UserId != session.Id).Select(item => (Item: item, Previous: item.UserId)).ToArray();
        var tickets = Data.Tickets.Where(item => item.UserId != session.Id).Select(item => (Item: item, Previous: item.UserId)).ToArray();
        if (conversations.Length > 0 || tickets.Length > 0)
        {
            try
            {
                foreach (var item in conversations) item.Item.UserId = session.Id;
                foreach (var item in tickets) item.Item.UserId = session.Id;
                Save();
            }
            catch
            {
                foreach (var item in conversations) item.Item.UserId = item.Previous;
                foreach (var item in tickets) item.Item.UserId = item.Previous;
                throw;
            }
        }
        return session;
    }
    private static Account CreateLocalSession()
    {
        var windowsIdentity = Environment.UserDomainName + "\\" + Environment.UserName;
        var identityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(windowsIdentity)));
        return new Account
        {
            Id = "local-" + identityHash,
            Login = Environment.UserName,
            Name = Environment.UserName,
            Admin = false,
            Diagnose = true,
            Repair = true,
            Tickets = true,
            Active = true,
            MustChangePassword = false
        };
    }
    public void Save()
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
    public void Log(Account actor, string action)
        => Commit(actor, action, () => { }, () => { });

    internal void Commit(Account actor, string action, Action apply, Action rollback)
    {
        var entry = new Audit { Actor = actor.Login, Action = action };
        try
        {
            apply();
            Data.Audit.Add(entry);
            Save();
        }
        catch
        {
            Data.Audit.Remove(entry);
            rollback();
            throw;
        }
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
        if (directAccess && actor.Id == CreateLocalSession().Id)
        {
            if (!permission(CreateLocalSession())) throw new InvalidOperationException("Esta função não está disponível na sessão local.");
            return;
        }
        var current = Data.Accounts.SingleOrDefault(a => a.Id == actor.Id);
        if (current == null || !current.Active || !permission(current)) throw new InvalidOperationException("Sua conta não tem permissão para esta ação.");
        if (current.MustChangePassword) throw new InvalidOperationException("Defina sua senha pessoal antes de usar o Nexo.");
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
        var oldPosition = old == null ? -1 : Data.Accounts.IndexOf(old);
        if (old != null) Data.Accounts.Remove(old);
        Data.Accounts.Add(edited);
        try { Log(actor, "Conta e permissões atualizadas: " + edited.Login); }
        catch
        {
            Data.Accounts.Remove(edited);
            if (old != null) Data.Accounts.Insert(oldPosition, old);
            throw;
        }
    }
    public void UpdateProfile(Account actor, string name, string email, string photo, string currentPassword, string newPassword)
    {
        var current = Data.Accounts.SingleOrDefault(a => a.Id == actor.Id);
        if (current == null || !current.Active) throw new InvalidOperationException("Sua conta não tem permissão para esta ação.");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Informe seu nome.");
        ValidateEmail(email);
        if (current.MustChangePassword && newPassword.Length == 0) throw new InvalidOperationException("Defina uma nova senha para concluir o primeiro acesso.");
        var before = (current.Name, current.Email, current.Photo, current.PasswordHash, current.MustChangePassword);
        if (newPassword.Length > 0)
        {
            if (!Passwords.Verify(currentPassword, current.PasswordHash)) throw new InvalidOperationException("A senha atual está incorreta.");
            Passwords.Validate(newPassword);
            current.PasswordHash = Passwords.Hash(newPassword);
            current.MustChangePassword = false;
        }
        current.Name = name.Trim(); current.Email = email.Trim(); current.Photo = photo;
        try { Log(current, "Perfil atualizado"); }
        catch
        {
            (current.Name, current.Email, current.Photo, current.PasswordHash, current.MustChangePassword) = before;
            throw;
        }
    }
    public static void ValidateEmail(string email)
    {
        if (email.Length > 0 && (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)) throw new InvalidOperationException("Informe um e-mail válido.");
    }
}
