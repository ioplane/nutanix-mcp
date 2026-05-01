using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;

namespace NutanixMcp.Core.Retrieval;

// Annotates Nutanix-specific acronyms on first occurrence:
//   "AHV cluster" → "AHV (Acropolis Hypervisor) cluster"
// Case-insensitive match; the acronym's original casing is preserved. Each
// acronym is expanded at most once per query so repeated mentions don't bloat
// the embedding. Acronyms cribbed from the Nutanix Bible glossary, KB index,
// and the canonical AHV / Prism / Files / NDB / LCM / Move / Flow product
// nomenclature.
public static partial class QueryExpander
{
    // Frozen for fast lookup on the query hot path — read-only, built once at startup.
    private static readonly FrozenDictionary<string, string> Synonyms =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AHV"] = "Acropolis Hypervisor",
            ["AOS"] = "Acropolis Operating System",
            ["NDB"] = "Nutanix Database Service",
            ["NCM"] = "Nutanix Cloud Manager",
            ["NKE"] = "Nutanix Kubernetes Engine",
            ["NKP"] = "Nutanix Kubernetes Platform",
            ["NCC"] = "Nutanix Cluster Check",
            ["NUS"] = "Nutanix Unified Storage",
            ["LCM"] = "Life Cycle Manager",
            ["CVM"] = "Controller Virtual Machine",
            ["IPMI"] = "Intelligent Platform Management Interface",
            ["SSP"] = "Self Service Portal",
            ["PE"] = "Prism Element",
            ["PC"] = "Prism Central",
            ["DSF"] = "Distributed Storage Fabric",
            ["RF"] = "Replication Factor",
            ["DR"] = "Disaster Recovery",
            ["CAC"] = "Cluster-Aware Configuration",
            ["LB"] = "Load Balancer",
            ["VPC"] = "Virtual Private Cloud",
            ["KMS"] = "Key Management Server",
            ["SED"] = "Self-Encrypting Drive",
            ["UVM"] = "User Virtual Machine",
            ["IPAM"] = "IP Address Management",
            ["NIC"] = "Network Interface Card",
            ["RDMA"] = "Remote Direct Memory Access",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b[\w][\w\-]*\b", RegexOptions.ExplicitCapture)]
    private static partial Regex WordRegex();

    public static QueryExpansion Expand(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(query.Length + 32);
        var lastIndex = 0;

        foreach (Match m in WordRegex().Matches(query))
        {
            if (m.Index > lastIndex)
            {
                sb.Append(query, lastIndex, m.Index - lastIndex);
            }

            var token = m.Value;
            sb.Append(token);

            if (Synonyms.TryGetValue(token, out var expansion) && seen.Add(token))
            {
                sb.Append(" (").Append(expansion).Append(')');
            }

            lastIndex = m.Index + m.Length;
        }

        if (lastIndex < query.Length)
        {
            sb.Append(query, lastIndex, query.Length - lastIndex);
        }

        var expanded = sb.ToString();
        return new QueryExpansion(query, expanded);
    }
}
