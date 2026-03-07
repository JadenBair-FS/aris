using ARIS.Ingestor.Services;
using ARIS.Shared.Entities;
using ARIS.Shared.Models.Ingestion.Onet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using ARIS.Shared.Data;
using Pgvector.EntityFrameworkCore;
using System.Text.Json;

using PgVectorType = Pgvector.Vector;

namespace ARIS.Ingestor;

public class IngestionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IngestionWorker> _logger;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;

// Valid node types in Roadmap.sh JSON that represent learnable skills
    private static readonly HashSet<string> ValidRoadmapNodeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "topic", "subtopic", "skill" };

    // Pedagogical label prefixes — instructional headings that never appear on resumes.
    private static readonly string[] PedagogicalPrefixes =
    [
        "learn ", "introduction to", "what is", "what are", "why ", "how to",
        "overview of", "getting started", "basics of", "fundamentals of",
        "understanding ", "working with ", "intro to", "history of",
        "types of ", "when to use", "why use",
        // Navigation / advice / imperative headings
        "pick a", "visit ", "click ", "explore ", "check ", "gain ",
        "follow ", "find ", "see the", "at this point", "you may", "you should",
        "you need", "continue learning",
        // Roadmap step/phase/checkpoint labels
        "step ", "phase ", "part ", "checkpoint ",
        // "for X" language construct headings (for loop, for range, for android)
        "for ",
        // Advanced/General/Basic section headings
        "advanced ", "general ", "basic ",
        // "Understand X" instructional headings (understand is not caught by "understanding ")
        "understand ",
        // Motivational/imperative UX copy — not skills
        "be ", "make ", "ways of", "clear ", "create a", "add a", "set up a",
    ];

    // Exact-match syntax noise — language keywords, primitive types, control flow
    // constructs, and operator tokens that are not transferable skills.
    private static readonly HashSet<string> SyntaxNoiseExact =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Control flow keywords
        "for", "while", "do...while", "if", "if...else", "switch", "Switch",
        "break", "continue", "break / continue", "throw", "throw statement",
        "try/catch/finally", "redo", "next", "unless", "case", "until",

        // Variable declaration keywords
        "var", "let", "const",

        // Primitive type keywords
        "null", "nil", "undefined", "boolean", "number", "string", "bigint",
        "integer", "float", "symbol", "Symbol", "Object", "Block",
        "Function", "Global",

        // Language construct categories (too generic)
        "Variables", "Functions", "Operators", "Control Flow Statements",
        "Built-in Types", "Built-in Functions", "Conditional Statements",
        "Loops", "Loops & Enumerations", "Conditionals", "Exceptions",
        "Methods", "Classes", "Inheritance", "Recursion", "Data Types",
        "Type Casting", "Arithmetic",

        // Collection type names (the type, not the skill of using it)
        "Lists", "Tuples", "Sets", "Dictionaries", "Arrays", "Collections",
        "Lambdas", "Iterators", "Generators",

        // Asset/file-type nouns — not skills
        "Images", "Fonts", "Other File Types", "Icons", "Sounds", "Assets",

        // ECMAScript spec-internal algorithm names
        "SameValue", "SameValueZero", "isLooselyEqual", "isStrictlyEqual",

        // Generic networking constructs (protocol descriptions, not tool skills)
        "HTTP", "HTTPS", "OSI Model", "White / Grey Listing", "Domain Keys",
        "Forward Proxy", "Reverse Proxy", "Caching Server",

        // Abstract design-pattern category names
        "Availability", "Data Management", "Design and Implementation",
        "Management and Monitoring",

        // CS theory fragments not specific to any language
        "HashMaps", "Binary Search Tree", "Arrays and Linked Lists",
        "Heaps Stacks and Queues", "Sorting Algorithms",

        // Flutter/Dart internal framework primitives
        "ChangeNotifier", "ValueNotifier", "Animation Controller",
        "Animated Builder", "Animated Widget", "Core Libraries",
        "flutter pub / dart pub", "JSON Serialize / Deserialize",
        "Isolates", "Futures", "Async / Await", "3 Trees",
        "Render Objects", "Curved Animation", "Hero", "Opacity",
        "Flutter Inspector", "Flutter Outline", "Memory Allocation",

        // Ruby syntax fragments
        "Defining methods", "Method Parameters", "Scope", "Chaining Methods",
        "Defining Classes", "Instance variables", "Attributes accessors",
        "Method Lookup",

        // JavaScript context sub-nodes
        "in a method", "in a function", "using it alone",
        "in event handlers", "in arrow functions",

        // Python/generic fragments
        "Basic Syntax", "Variables and Data Types", "Encapsulation",
        "File Handling", "Variable Declarations", "Variable Naming Rules",
        "GIL", "Builtin", "Custom", "Asynchrony",

        // Storage / duplicate generic headings
        "Storage", "Deployment", "Logging",

        // Cloud design pattern categories (too abstract)
        "Cloud Specific Tools",

        // Generic game-dev dimension descriptors — not standalone skills
        "2D", "3D",

        // Generic roadmap section headings
        "Introduction", "Overview", "Basics", "Fundamentals", "Core Concepts",
        "Advanced Topics", "Skills", "Applications", "Scripting", "Testing",
        "Security", "Agents", "Realtime", "Streaming", "Telemetry",
        "Observability", "Provisioning", "Containerization", "Scheduling",
        "Networking", "Authentication", "Authorization",

        // Generic container/infrastructure nouns (too broad as standalone skills)
        "Containers", "Volumes", "Networks", "Databases", "Pods", "Images",
        "Nodes", "Services", "Workloads", "Deployments", "Endpoints",

        // Roadmap website UI nodes
        "roadmap.sh", "Related Roadmaps", "Skills",

        // Generic process/practice category labels
        "Package Managers", "Programming Languages", "Application Architecture",
        "Command Line Utilities", "Debuggers", "Terminal Knowledge",
        "Text Manipulation", "Process Monitoring", "Performance Monitoring",
        "Resource Management", "Secret Management",
    };

    // Regex patterns for noise that cannot be caught by exact match.
    // Evaluated against the lowercased label.
    private static readonly (System.Text.RegularExpressions.Regex Pattern, string Reason)[] NoisePatterns =
    [
        // Bare lowercase single word = language keyword (e.g. "for", "nil", "string")
        (new System.Text.RegularExpressions.Regex(@"^[a-z][a-z0-9]*$"),
            "bare lowercase keyword"),

        // Widget subcategory: "Stateless Widgets", "Material Widgets", etc.
        (new System.Text.RegularExpressions.Regex(@"^(stateless|stateful|responsive|inherited|styled|material|cupertino|adaptive)\s+(widget|component)s?$"),
            "widget subcategory"),

        // Animation internal API class: "Animation Controller", "Animated Builder"
        (new System.Text.RegularExpressions.Regex(@"^(animation|animated)\s+\w+"),
            "animation internal"),

        // Gerund phrase = implementation sub-step, not a skill
        (new System.Text.RegularExpressions.Regex(@"^(defining|handling|chaining|creating|building|implementing|setting up|configuring|managing|running|deploying|using)\s"),
            "gerund fragment"),

        // "in a X" / "using it X" context sub-nodes
        (new System.Text.RegularExpressions.Regex(@"^(in a |using it |in event )"),
            "context sub-node"),

        // Config/artifact file names: pyproject.toml, Dockerfile.yaml, etc.
        (new System.Text.RegularExpressions.Regex(@"\.(toml|cfg|ini|yaml|yml|json|xml|lock)$"),
            "config file name"),

        // Contains a question mark = roadmap heading
        (new System.Text.RegularExpressions.Regex(@"\?"),
            "question heading"),

        // "Debugging X" sub-activities (keep bare "Debugging" which is a real skill)
        (new System.Text.RegularExpressions.Regex(@"^debugging (issues|memory leaks|performance|errors|crashes)$"),
            "debugging sub-activity"),

        // Loop/iteration syntax: "for...in loop", "for...of loop"
        (new System.Text.RegularExpressions.Regex(@"^(for\.\.\.|do\.\.\.)"),
            "loop syntax"),

        // Operator-level ECMAScript: "== operator", "=== operator"
        (new System.Text.RegularExpressions.Regex(@"^[=!<>]{1,3}$"),
            "operator token"),

        // Variable/Method/Instance sub-property fragments
        (new System.Text.RegularExpressions.Regex(@"^(instance|class|method|object)\s+(variables?|parameters?|accessors?|attributes?|lookup)$"),
            "OOP sub-property"),

        // Shell variable tokens and MongoDB/query operators: $#, $*, $0, $eq, $gt, etc.
        (new System.Text.RegularExpressions.Regex(@"^\$"),
            "shell/query operator token"),

        // @ template directives: @if, @each, etc.
        (new System.Text.RegularExpressions.Regex(@"^@"),
            "template directive"),

        // Bare operator symbols: *, +, -, etc.
        (new System.Text.RegularExpressions.Regex(@"^[\*\+\-\/\|\\]{1,3}$"),
            "bare operator symbol"),

        // "X vs Y" comparison headings: "Bare Metal vs VMs vs Containers", "AI vs Traditional Coding"
        (new System.Text.RegularExpressions.Regex(@"\bvs\.?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "comparison heading"),

        // "X Best Practices" prescription headings
        (new System.Text.RegularExpressions.Regex(@"[Bb]est [Pp]ractices?$"),
            "best practices heading"),

        // "Others (...)" catch-all category nodes
        (new System.Text.RegularExpressions.Regex(@"^[Oo]thers?\s*[\(\[]"),
            "catch-all category"),

        // Ends with "Options" or "Providers" — generic category headers
        (new System.Text.RegularExpressions.Regex(@"\s+[Oo]ptions?\s*$"),
            "generic options category"),
        (new System.Text.RegularExpressions.Regex(@"\s+[Pp]roviders?\s*$"),
            "generic providers category"),

        // Ends with "Roadmap" — roadmap navigation cross-reference nodes
        (new System.Text.RegularExpressions.Regex(@"\s+[Rr]oadmap\s*$"),
            "roadmap navigation node"),

        // Ends with "Ideas" — project idea suggestion nodes
        (new System.Text.RegularExpressions.Regex(@"\s+[Ii]deas?\s*$"),
            "project ideas node"),

        // Ends with "Fundamentals" — section heading
        (new System.Text.RegularExpressions.Regex(@"\s+[Ff]undamentals?\s*$"),
            "fundamentals heading"),

        // Ends with plural "Patterns", "Concepts", "Techniques", "Principles" — category headings
        // (singular kept: "Builder Pattern", "CAP Theorem" etc. are real named skills)
        (new System.Text.RegularExpressions.Regex(@"\s+[Pp]atterns\s*$"),
            "patterns category heading"),
        (new System.Text.RegularExpressions.Regex(@"\s+[Cc]oncepts\s*$"),
            "concepts category heading"),
        (new System.Text.RegularExpressions.Regex(@"\s+[Tt]echniques\s*$"),
            "techniques category heading"),
        (new System.Text.RegularExpressions.Regex(@"\s+[Pp]rinciples\s*$"),
            "principles category heading"),

        // Numbered list items: "1) Predicting...", "2. Learn..."
        (new System.Text.RegularExpressions.Regex(@"^[0-9]+[.)]\s"),
            "numbered list item"),

        // "X Strategies" and "X Usecases" — category section headings
        (new System.Text.RegularExpressions.Regex(@"\s+[Ss]trategies\s*$"),
            "strategies category heading"),
        (new System.Text.RegularExpressions.Regex(@"\s+[Uu]se\s*[Cc]ases?\s*$"),
            "use cases category heading"),
        (new System.Text.RegularExpressions.Regex(@"\s+[Uu]secases?\s*$"),
            "usecases category heading"),

        // "Git Basics", "Python Basics" — ends with Basics (plural section heading)
        (new System.Text.RegularExpressions.Regex(@"\s+[Bb]asics\s*$"),
            "basics section heading"),

        // CLI commands with flags: "bash -n", "kubectl apply -f", "docker run -d"
        (new System.Text.RegularExpressions.Regex(@"[a-z] -[a-zA-Z]"),
            "cli flag argument"),

        // Parenthetical with 2+ commas = catch-all list: "(ghcr, ecr, gcr, acr, etc)"
        (new System.Text.RegularExpressions.Regex(@"\([^)]*,[^)]*,[^)]*\)"),
            "catch-all list"),

        // Labels longer than 60 chars = prose description, not a skill name
        (new System.Text.RegularExpressions.Regex(@".{61,}"),
            "prose description"),
    ];

    private static readonly string[] RoleRoadmapSlugs =
    [
        "frontend", "backend", "full-stack", "devops", "devsecops",
        "android", "ios", "game-developer", "server-side-game-developer",
        "ai-engineer", "ai-data-scientist", "ai-agents", "ai-red-teaming",
        "machine-learning", "mlops", "data-analyst", "data-engineer", "bi-analyst",
        "qa", "cyber-security",
        "software-architect", "ux-design", "technical-writer",
        "product-manager", "engineering-manager"
    ];

    // Manual O*NET code mappings for role slugs where embedding search is unreliable.
    // Codes verified against O*NET 29.0. Embedding fallback handles anything not listed here.
    private static readonly Dictionary<string, string[]> SlugToOnetCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Web / App Development
            ["frontend"]                    = ["15-1254.00"],  // Web Developers
            ["backend"]                     = ["15-1252.00"],  // Software Developers
            ["full-stack"]                  = ["15-1254.00"],  // Web Developers
            ["android"]                     = ["15-1252.00"],  // Software Developers
            ["ios"]                         = ["15-1252.00"],  // Software Developers

            // Infrastructure / Operations
            ["devops"]                      = ["15-1244.00"],  // Network and Computer Systems Administrators
            ["devsecops"]                   = ["15-1212.00"],  // Information Security Analysts
            ["mlops"]                       = ["15-1244.00"],  // Network and Computer Systems Administrators

            // AI / Data
            ["ai-engineer"]                 = ["15-2051.00"],  // Data Scientists
            ["ai-data-scientist"]           = ["15-2051.00"],  // Data Scientists
            ["ai-agents"]                   = ["15-2051.00"],  // Data Scientists
            ["ai-red-teaming"]              = ["15-1212.00"],  // Information Security Analysts
            ["machine-learning"]            = ["15-2051.00"],  // Data Scientists
            ["data-analyst"]                = ["15-2051.01"],  // Business Intelligence Analysts
            ["data-engineer"]               = ["15-1243.00"],  // Database Architects
            ["bi-analyst"]                  = ["15-2051.01"],  // Business Intelligence Analysts

            // Quality / Security
            ["qa"]                          = ["15-1253.00"],  // Software Quality Assurance Analysts and Testers
            ["cyber-security"]              = ["15-1212.00"],  // Information Security Analysts

            // Architecture / Design
            ["software-architect"]          = ["15-1252.00"],  // Software Developers
            ["ux-design"]                   = ["15-1255.00"],  // Web and Digital Interface Designers

            // Games
            ["game-developer"]              = ["15-1252.00"],  // Software Developers
            ["server-side-game-developer"]  = ["15-1252.00"],  // Software Developers

            // Management / Other
            ["product-manager"]             = ["11-3021.00"],  // Computer and Information Systems Managers
            ["engineering-manager"]         = ["11-3021.00"],  // Computer and Information Systems Managers
            ["technical-writer"]            = ["27-3042.00"],  // Technical Writers

            // blockchain and devrel intentionally omitted — no reliable O*NET analog;
            // embedding fallback will attempt a match and skip if nothing is close enough.
        };

    private static readonly string[] SkillRoadmapSlugs =
    [
        "computer-science", "system-design", "software-design-architecture",
        "datastructures-and-algorithms", "design-system", "prompt-engineering",
        "javascript", "typescript", "python", "java", "cpp", "rust", "golang",
        "php", "ruby", "kotlin", "sql", "graphql",
        "react", "react-native", "angular", "vue", "nextjs", "swift-ui",
        "nodejs", "aspnet-core", "spring-boot", "django", "laravel", "ruby-on-rails",
        "flutter", "html", "css", "shell-bash",
        "postgresql-dba", "mongodb", "redis", "elasticsearch",
        "docker", "kubernetes", "aws", "terraform", "cloudflare",
        "linux", "git-github", "wordpress"
    ];

    public IngestionWorker(
        IServiceProvider serviceProvider,
        ILogger<IngestionWorker> logger,
        IHostApplicationLifetime hostApplicationLifetime,
        IEmbeddingGenerator<string, Embedding<float>> embeddingService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _hostApplicationLifetime = hostApplicationLifetime;
        _embeddingService = embeddingService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Ingestion Worker Started.");

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ArisDbContext>();
        var onetService = scope.ServiceProvider.GetRequiredService<OnetService>();
        var roadmapService = scope.ServiceProvider.GetRequiredService<RoadmapService>();
        var neo4jService = scope.ServiceProvider.GetRequiredService<Neo4jIngestionService>();
        var ontologyService = scope.ServiceProvider.GetRequiredService<OntologyEnrichmentService>();

        var args = Environment.GetCommandLineArgs();

        // Special Modes

        if (args.Contains("--optimize-graph"))
        {
            _logger.LogInformation(">>> OPTIMIZATION MODE <<<");
            await RunOptimizeGraphAsync(args, neo4jService, ontologyService, stoppingToken);
            _logger.LogInformation("Optimization Complete.");
            _hostApplicationLifetime.StopApplication();
            return;
        }

        if (args.Contains("--seed-gold"))
        {
            _logger.LogInformation(">>> GOLD STANDARD SEEDING MODE <<<");
            var seeder = scope.ServiceProvider.GetRequiredService<GoldStandardSeeder>();
            await seeder.RunSeedingAsync("C:\\dev\\Masters Capstone\\GoldStandard");
            _logger.LogInformation("Gold Standard Seeding Complete.");
            _hostApplicationLifetime.StopApplication();
            return;
        }

        await dbContext.Database.EnsureCreatedAsync(stoppingToken);
        await neo4jService.EnsureIndicesAsync();

        var onetOnly = args.Contains("--onet-only");
        var roadmapOnly = args.Contains("--roadmap-only");

        if (!roadmapOnly)
        {
            if (args.Contains("--fresh"))
            {
                _logger.LogWarning(">>> FRESH MODE: Clearing all data <<<");
                await EnsureCleanSlateAsync(dbContext, neo4jService, stoppingToken);
            }

            _logger.LogInformation("=== Phase 1: O*NET Ingestion ===");
            await IngestOnetAsync(dbContext, onetService, neo4jService, stoppingToken);

            if (onetOnly)
            {
                _logger.LogInformation("O*NET-only run complete. Inspect the database before running --roadmap-only.");
                _hostApplicationLifetime.StopApplication();
                return;
            }
        }

        _logger.LogInformation("=== Phase 2: Building Slug-Role Map ===");
        var slugRoleMap = await BuildSlugRoleMapAsync(dbContext, roadmapService, RoleRoadmapSlugs, stoppingToken);

        _logger.LogInformation("=== Phase 3: Role Roadmap Ingestion ===");
        foreach (var slug in RoleRoadmapSlugs)
        {
            if (stoppingToken.IsCancellationRequested) break;
            _logger.LogInformation("Processing Role Roadmap: {Slug}", slug);
            var matchedRoles = slugRoleMap.GetValueOrDefault(slug) ?? [];
            await IngestRoadmapAsync(dbContext, roadmapService, neo4jService, slug,
                isRoleRoadmap: true, matchedRoles, stoppingToken);
        }

        _logger.LogInformation("=== Phase 4: Skill Roadmap Ingestion ===");
        foreach (var slug in SkillRoadmapSlugs)
        {
            if (stoppingToken.IsCancellationRequested) break;
            _logger.LogInformation("Processing Skill Roadmap: {Slug}", slug);
            await IngestRoadmapAsync(dbContext, roadmapService, neo4jService, slug,
                isRoleRoadmap: false, [], stoppingToken);
        }

        _logger.LogInformation("=== Phase 5: Ontology Enrichment ===");
        await RunOptimizeGraphAsync(args, neo4jService, ontologyService, stoppingToken);

        _logger.LogInformation("Ingestion Complete.");
        _hostApplicationLifetime.StopApplication();
    }

    // ── Phase 1: O*NET ─────────────────────────────────────────────────────────

    private async Task IngestOnetAsync(
        ArisDbContext dbContext,
        OnetService onetService,
        Neo4jIngestionService neo4j,
        CancellationToken ct)
    {
        _logger.LogInformation("=== Phase 1a: Soft Skill Taxonomy ===");
        await IngestOnetSkillTaxonomiesAsync(dbContext, onetService, neo4j, ct);

        var occupations = await onetService.GetAllOccupationsAsync(ct);
        _logger.LogInformation("Found {Count} O*NET occupations.", occupations.Count);

        var i = 0;
        foreach (var occ in occupations)
        {
            if (ct.IsCancellationRequested) break;
            i++;
            _logger.LogInformation("[O*NET {Current}/{Total}] {Title} ({Code})",
                i, occupations.Count, occ.Title, occ.Code);

            var details = await onetService.GetOccupationDetailsAsync(occ.Code, ct);
            if (details == null) continue;

            await neo4j.MergeRoleAsync(details.Title, details.Code, details.Description ?? "");

            var existingRole = await dbContext.Roles
                .FirstOrDefaultAsync(r => r.OnetCode == occ.Code, ct);

            if (existingRole == null)
            {
                var roleEmbedding = await GenerateEmbeddingAsync($"{details.Title}: {details.Description}");
                existingRole = new RefRole
                {
                    Title = details.Title,
                    OnetCode = details.Code,
                    Description = details.Description,
                    Embedding = roleEmbedding
                };
                dbContext.Roles.Add(existingRole);
                await dbContext.SaveChangesAsync(ct);
            }

            // Technology skills (specific software / tools) → IsTech = true
            var rawToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var techSkillName in details.TechnologySkills)
            {
                var canon = await DeduplicateOrCreateSkillAsync(
                    dbContext, neo4j, techSkillName, "ONET_Skill", isTech: true, ct);
                await neo4j.MergeRoleSkillRelationshipAsync(details.Code, canon);
                await LinkSkillToRoleInPostgresAsync(dbContext, existingRole.Id, canon, ct);
                rawToCanonical[techSkillName] = canon;
            }

            // Category-based BRIDGE_TO: tools in the same O*NET category are deterministic peers
            foreach (var category in details.TechSkillCategories)
            {
                var categoryCanonicals = (category.Example ?? []).Concat(category.ExampleMore ?? [])
                    .Select(e => e.Title)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => rawToCanonical.GetValueOrDefault(t!, t!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                for (var a = 0; a < categoryCanonicals.Count; a++)
                {
                    for (var b = a + 1; b < categoryCanonicals.Count; b++)
                    {
                        await neo4j.MergeBridgeRelationshipAsync(
                            categoryCanonicals[a], categoryCanonicals[b], "ONET_Category");
                    }
                }
            }

            await dbContext.SaveChangesAsync(ct);
            await Task.Delay(200, ct);
        }

        _logger.LogInformation("O*NET Ingestion Complete: {Count} occupations processed.", i);
    }

    // Phase 2: Build Slug → Role Map

    private async Task<Dictionary<string, List<string>>> BuildSlugRoleMapAsync(
        ArisDbContext dbContext,
        RoadmapService roadmapService,
        string[] slugs,
        CancellationToken ct)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Pre-load all role codes that exist in the DB so we can validate manual entries
        var existingCodes = await dbContext.Roles
            .Where(r => r.OnetCode != null)
            .Select(r => r.OnetCode!)
            .ToHashSetAsync(ct);

        foreach (var slug in slugs)
        {
            if (ct.IsCancellationRequested) break;
            if (SlugToOnetCodes.TryGetValue(slug, out var manualCodes))
            {
                var validCodes = manualCodes.Where(c => existingCodes.Contains(c)).ToList();
                map[slug] = validCodes;
                _logger.LogInformation("Slug '{Slug}' → manual map → {Count} O*NET roles: {Roles}",
                    slug, validCodes.Count, string.Join(", ", validCodes));
                continue;
            }

            var roadmap = await roadmapService.GetRoadmapAsync(slug, ct);
            var title = roadmap?.Title?.Card ?? slug.Replace("-", " ");

            var embedding = await GenerateEmbeddingAsync(title);
            if (embedding == null)
            {
                map[slug] = [];
                continue;
            }

            var matches = await dbContext.Roles
                .Where(r => r.Embedding != null)
                .Select(r => new { r.OnetCode, r.Title, Distance = r.Embedding!.CosineDistance(embedding) })
                .Where(x => x.Distance < 0.30)
                .OrderBy(x => x.Distance)
                .Take(3)
                .ToListAsync(ct);

            map[slug] = matches.Select(m => m.OnetCode).OfType<string>().ToList();
            _logger.LogInformation("Slug '{Slug}' ({Title}) → embedding fallback → {Count} O*NET roles: {Roles}",
                slug, title, map[slug].Count,
                string.Join(", ", matches.Select(m => $"{m.Title} ({m.OnetCode})")));

            await Task.Delay(100, ct);
        }

        return map;
    }


    private async Task IngestOnetSkillTaxonomiesAsync(
        ArisDbContext dbContext,
        OnetService onetService,
        Neo4jIngestionService neo4j,
        CancellationToken ct)
    {
        var basicSkills = await onetService.GetSkillTaxonomyAsync("online/onet_data/skills_basic/", ct);
        var cfSkills = await onetService.GetSkillTaxonomyAsync("online/onet_data/skills_cf/", ct);
        var allRoots = basicSkills.Concat(cfSkills).ToList();

        _logger.LogInformation("Fetched {Count} taxonomy root categories from O*NET.", allRoots.Count);

        foreach (var root in allRoots)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessTaxonomyNodeAsync(dbContext, neo4j, root, parentName: null, ct);
        }

        _logger.LogInformation("Soft skill taxonomy ingestion complete.");
    }

    private async Task ProcessTaxonomyNodeAsync(
        ArisDbContext dbContext,
        Neo4jIngestionService neo4j,
        SkillTaxonomyNode node,
        string? parentName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(node.Name)) return;

        await DeduplicateOrCreateSkillAsync(dbContext, neo4j, node.Name, "ONET_Taxonomy", isTech: false, ct);

        if (parentName != null)
            await neo4j.MergeSubsetRelationshipAsync(node.Name, parentName, "ONET_Taxonomy");

        if (node.Child == null) return;

        foreach (var child in node.Child)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessTaxonomyNodeAsync(dbContext, neo4j, child, node.Name, ct);
        }
    }


    private async Task IngestRoadmapAsync(
        ArisDbContext dbContext,
        RoadmapService roadmapService,
        Neo4jIngestionService neo4j,
        string slug,
        bool isRoleRoadmap,
        List<string> matchedRoleCodes,
        CancellationToken ct)
    {
        var roadmap = await roadmapService.GetRoadmapAsync(slug, ct);
        if (roadmap?.Nodes == null)
        {
            _logger.LogWarning("Roadmap '{Slug}' returned no nodes — skipping.", slug);
            return;
        }

        //Process all valid nodes → build nodeId → canonical skill name map
        var nodeIdToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in roadmap.Nodes)
        {
            if (!ValidRoadmapNodeTypes.Contains(node.Type ?? "")) continue;
            var label = node.Data?.Label?.Trim();
            if (string.IsNullOrWhiteSpace(label) || node.Id == null) continue;

            // Skip pedagogical headings
            var labelLower = label.ToLowerInvariant();
            if (PedagogicalPrefixes.Any(p => labelLower.StartsWith(p))) continue;

            // Skip exact-match syntax noise and concept fragments
            if (SyntaxNoiseExact.Contains(label)) continue;

            // Skip regex-matched noise patterns
            if (NoisePatterns.Any(np => np.Pattern.IsMatch(labelLower))) continue;

            var canon = await DeduplicateOrCreateSkillAsync(
                dbContext, neo4j, label, "Roadmap.sh", isTech: true, ct);
            if (!string.IsNullOrEmpty(canon))
                nodeIdToCanonical[node.Id] = canon;
        }

        _logger.LogInformation("Roadmap '{Slug}': {Count} valid skills processed.", slug, nodeIdToCanonical.Count);

        //Process edges for hierarchy (solid → SUBSET_OF) and bridges (dashed → BRIDGE_TO)
        var validNodeIds = nodeIdToCanonical.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var solidEdgeTargetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (roadmap.Edges != null)
        {
            foreach (var edge in roadmap.Edges)
            {
                if (edge.Source == null || edge.Target == null) continue;
                if (!validNodeIds.Contains(edge.Source) || !validNodeIds.Contains(edge.Target)) continue;

                var parentCanon = nodeIdToCanonical[edge.Source];
                var childCanon = nodeIdToCanonical[edge.Target];

                if (string.Equals(parentCanon, childCanon, StringComparison.OrdinalIgnoreCase)) continue;

                var edgeStyle = edge.Data?.EdgeStyle ?? "solid";
                if (edgeStyle.Equals("dashed", StringComparison.OrdinalIgnoreCase))
                {
                    // Dashed edge = alternative/optional → BRIDGE_TO
                    await neo4j.MergeBridgeRelationshipAsync(parentCanon, childCanon, "Roadmap.sh");
                }
                else
                {
                    // Solid edge = parent → child dependency → child SUBSET_OF parent
                    await neo4j.MergeSubsetRelationshipAsync(childCanon, parentCanon, "Roadmap.sh");
                    solidEdgeTargetIds.Add(edge.Target);
                }
            }
        }

        if (!isRoleRoadmap)
        {
            // Skill roadmap: top-level nodes (no solid edge parent in this roadmap) link to root skill
            var rootSkillName = roadmap.Title?.Card ?? slug.Replace("-", " ");
            await DeduplicateOrCreateSkillAsync(dbContext, neo4j, rootSkillName, "Roadmap.sh", isTech: true, ct);

            foreach (var (nodeId, canonName) in nodeIdToCanonical)
            {
                if (solidEdgeTargetIds.Contains(nodeId)) continue;
                if (string.Equals(canonName, rootSkillName, StringComparison.OrdinalIgnoreCase)) continue;
                await neo4j.MergeSubsetRelationshipAsync(canonName, rootSkillName, "Roadmap.sh");
            }
        }
        else
        {
            if (matchedRoleCodes.Count == 0)
            {
                _logger.LogWarning(
                    "Roadmap '{Slug}' has no matched O*NET roles — skipping skill ingestion to avoid orphaned nodes.", slug);
                return;
            }

            var pgRoles = await dbContext.Roles
                .Where(r => r.OnetCode != null && matchedRoleCodes.Contains(r.OnetCode))
                .ToListAsync(ct);

            foreach (var (_, canonName) in nodeIdToCanonical)
            {
                foreach (var roleCode in matchedRoleCodes)
                {
                    // Neo4j REQUIRES edge
                    await neo4j.MergeRoleSkillRelationshipAsync(roleCode, canonName);
                }

                foreach (var pgRole in pgRoles)
                {
                    await LinkSkillToRoleInPostgresAsync(dbContext, pgRole.Id, canonName, ct);
                }
            }

            await dbContext.SaveChangesAsync(ct);
        }

        await Task.Delay(500, ct);
    }

    //Shared Helpers

    /// <summary>
    /// Central deduplication + upsert helper. Cosine distance threshold = 0.10.
    /// IsTech = true wins and is never downgraded.
    /// Returns the canonical skill name to use for all graph relationships.
    /// </summary>
    private async Task<string> DeduplicateOrCreateSkillAsync(
        ArisDbContext dbContext,
        Neo4jIngestionService neo4j,
        string name,
        string source,
        bool isTech,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var exactMatch = await dbContext.Skills
            .FirstOrDefaultAsync(s => s.Name == name, ct);

        string canonicalName;

        if (exactMatch != null)
        {
            canonicalName = exactMatch.Name;

            if (isTech && !exactMatch.IsTech && source == "Roadmap.sh"
                && exactMatch.Source != "ONET_Taxonomy")
            {
                _logger.LogInformation("Upgrading '{Skill}' to is_tech=true (Roadmap.sh)", canonicalName);
                exactMatch.IsTech = true;
                await dbContext.SaveChangesAsync(ct);
                await neo4j.SetSkillIsTechAsync(canonicalName, true);
            }
        }
        else
        {
            var embedding = await GenerateEmbeddingAsync(name);
            if (embedding == null) return name;

            var skill = new RefSkill
            {
                Name = name,
                Source = source,
                IsTech = isTech,
                Embedding = embedding
            };
            dbContext.Skills.Add(skill);
            await dbContext.SaveChangesAsync(ct);
            canonicalName = name;
        }

        await neo4j.MergeSkillAsync(canonicalName, source, isTech);
        return canonicalName;
    }

    private async Task LinkSkillToRoleInPostgresAsync(
        ArisDbContext dbContext,
        int roleId,
        string canonicalSkillName,
        CancellationToken ct)
    {
        var skill = await dbContext.Skills
            .FirstOrDefaultAsync(s => s.Name == canonicalSkillName, ct);
        if (skill == null) return;

        // Check local change tracker first — the same canonical skill can appear in multiple
        // lists (Skills, Knowledge, WorkActivities, TechSkills) within one occupation, and
        // SaveChanges hasn't been called yet, so AnyAsync alone would return false twice.
        var alreadyTracked = dbContext.RoleSkills.Local
            .Any(rs => rs.RoleId == roleId && rs.SkillId == skill.Id);
        if (alreadyTracked) return;

        var existsInDb = await dbContext.RoleSkills
            .AnyAsync(rs => rs.RoleId == roleId && rs.SkillId == skill.Id, ct);
        if (!existsInDb)
        {
            dbContext.RoleSkills.Add(new RefRoleSkill
            {
                RoleId = roleId,
                SkillId = skill.Id,
                Importance = 0,
                Level = 0
            });
        }
    }

    private async Task EnsureCleanSlateAsync(
        ArisDbContext dbContext,
        Neo4jIngestionService neo4j,
        CancellationToken ct)
    {
        _logger.LogWarning("!!! CLEARING ALL DICTIONARY DATA !!!");
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE ref_role_skills, ref_skills, ref_roles RESTART IDENTITY CASCADE;", ct);
        _logger.LogInformation("PostgreSQL Dictionary Tables Truncated.");
        await neo4j.ClearDatabaseAsync();
    }

    private async Task<PgVectorType?> GenerateEmbeddingAsync(string text)
    {
        try
        {
            var embeddings = await _embeddingService.GenerateAsync([text]);
            var vectorData = embeddings[0].Vector;
            return new PgVectorType(vectorData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate embedding for: {Text}",
                text.Length > 50 ? text[..50] + "..." : text);
            return null;
        }
    }

    private static readonly string CheckpointPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aris_ingestor_checkpoint.json");

    private sealed record IngestionCheckpoint(int SiblingsIndex, string Timestamp);

    private static async Task WriteCheckpointAsync(int siblingsIdx)
    {
        var cp = new IngestionCheckpoint(siblingsIdx, DateTimeOffset.UtcNow.ToString("O"));
        await File.WriteAllTextAsync(CheckpointPath,
            JsonSerializer.Serialize(cp, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<IngestionCheckpoint?> ReadCheckpointAsync()
    {
        if (!File.Exists(CheckpointPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<IngestionCheckpoint>(
                await File.ReadAllTextAsync(CheckpointPath));
        }
        catch { return null; }
    }

    private static void DeleteCheckpoint()
    {
        if (File.Exists(CheckpointPath)) File.Delete(CheckpointPath);
    }

    private async Task RunOptimizeGraphAsync(
        string[] args,
        Neo4jIngestionService neo4jService,
        OntologyEnrichmentService ontologyService,
        CancellationToken stoppingToken)
    {
        int? limit = null;
        var limitIndex = Array.IndexOf(args, "--limit");
        if (limitIndex >= 0 && limitIndex + 1 < args.Length &&
            int.TryParse(args[limitIndex + 1], out var parsedLimit))
            limit = parsedLimit;

        var skipBridges = args.Contains("--skip-bridges");

        int resumeSiblingsIndex = 0;
        var resumeSiblingsIdx = Array.IndexOf(args, "--resume-siblings");
        if (resumeSiblingsIdx >= 0 && resumeSiblingsIdx + 1 < args.Length &&
            int.TryParse(args[resumeSiblingsIdx + 1], out var parsedResumeSiblings))
            resumeSiblingsIndex = parsedResumeSiblings;

        var checkpoint = await ReadCheckpointAsync();
        if (checkpoint != null)
        {
            _logger.LogInformation("Auto-resuming from checkpoint ({Timestamp})", checkpoint.Timestamp);
            if (checkpoint.SiblingsIndex > 0)
            {
                resumeSiblingsIndex = checkpoint.SiblingsIndex;
                _logger.LogInformation("  Checkpoint overriding siblings index to {Index}", checkpoint.SiblingsIndex);
            }
        }

        int maxClusterSize = 150;
        var maxClusterIdx = Array.IndexOf(args, "--max-cluster");
        if (maxClusterIdx >= 0 && maxClusterIdx + 1 < args.Length &&
            int.TryParse(args[maxClusterIdx + 1], out var parsedMaxCluster))
            maxClusterSize = parsedMaxCluster;

        var processedBridges = new HashSet<string>();
        _logger.LogInformation("Loading existing BRIDGE_TO edges into dedup set...");
        var existingBridgeKeys = await neo4jService.GetExistingBridgeKeysAsync();
        foreach (var key in existingBridgeKeys)
            processedBridges.Add(key);
        _logger.LogInformation("Loaded {Count} existing bridge keys.", existingBridgeKeys.Count);

        if (!skipBridges)
        {
            _logger.LogInformation("=== Phase 5: Roadmap.sh Sibling Bridge Detection ===");
            var siblings = await neo4jService.GetRoadmapSiblingClustersAsync();
            _logger.LogInformation("Found {Count} Roadmap.sh sibling clusters.", siblings.Count);
            var clusterCount = 0;

            foreach (var cluster in siblings)
            {
                if (stoppingToken.IsCancellationRequested) break;
                if (cluster.Siblings.Count <= 1) continue;

                clusterCount++;

                if (clusterCount < resumeSiblingsIndex)
                {
                    if (clusterCount % 100 == 0 || clusterCount == resumeSiblingsIndex - 1)
                        _logger.LogInformation("  Skipping {Current}/{ResumeIndex}...",
                            clusterCount, resumeSiblingsIndex);
                    continue;
                }

                if (cluster.Siblings.Count > maxClusterSize)
                {
                    _logger.LogInformation(
                        "[Sibling Cluster {Current}/{Total}] Skipping '{Parent}' — {Count} siblings exceeds max ({Max}).",
                        clusterCount, siblings.Count, cluster.Parent, cluster.Siblings.Count, maxClusterSize);
                    continue;
                }

                _logger.LogInformation(
                    "[Sibling Cluster {Current}/{Total}] Analyzing '{Parent}' with {Count} siblings",
                    clusterCount, siblings.Count, cluster.Parent, cluster.Siblings.Count);

                var hierarchies = await neo4jService.GetInternalHierarchiesAsync(cluster.Siblings);
                var parentNames = hierarchies.Select(h => h.Parent).ToHashSet();
                var prunedSkills = cluster.Siblings.Where(s => !parentNames.Contains(s)).ToList();

                if (prunedSkills.Count < 2)
                {
                    _logger.LogInformation("  - Skipping: Pruning left less than 2 skills.");
                    continue;
                }

                var bridges = await ontologyService.FindBridgesAsync(
                    cluster.Parent, prunedSkills, hierarchies, stoppingToken);

                foreach (var bridge in bridges)
                {
                    var key = string.Join("|", new[] { bridge.Source, bridge.Target }.OrderBy(s => s));
                    if (processedBridges.Add(key))
                    {
                        _logger.LogInformation("  + New Bridge: {Source} <-> {Target}", bridge.Source, bridge.Target);
                        await neo4jService.MergeBridgeRelationshipAsync(bridge.Source, bridge.Target,
                            "OntologyEnrichment");
                    }
                }

                await WriteCheckpointAsync(clusterCount + 1);
            }
        }

        DeleteCheckpoint();
        _logger.LogInformation("Ontology enrichment complete. Checkpoint cleared.");
    }
}
