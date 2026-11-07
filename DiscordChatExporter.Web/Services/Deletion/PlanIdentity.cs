using System;
using System.Security.Cryptography;
using System.Text;

namespace DiscordChatExporter.Web.Services.Deletion;

public static class PlanIdentity
{
    // A plan is only valid for the exact (run, criteria, acting account, manifest state) it was
    // previewed against. Any change produces a different id, forcing a re-preview before deletion.
    public static string Compute(
        string runId,
        string criteriaCanonicalJson,
        string userId,
        string tokenKind,
        long manifestRevision
    )
    {
        var material =
            $"{runId}\n{criteriaCanonicalJson}\n{userId}\n{tokenKind}\n{manifestRevision}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash);
    }
}
