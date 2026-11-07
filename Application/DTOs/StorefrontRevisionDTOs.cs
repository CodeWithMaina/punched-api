// ═══════════════════════════════════════════════════════════════
//  STOREFRONT REVISION HISTORY DTOs
//
//  Read models for the builder's History panel: a lightweight metadata list
//  (never the payload), a full detail (the configuration), and a structured
//  comparison expressed as human-readable changes rather than a raw JSON diff.
// ═══════════════════════════════════════════════════════════════

using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>
/// Lightweight revision metadata. Deliberately carries no configuration payload so
/// listing history stays cheap no matter how large a storefront grows.
/// </summary>
public class StorefrontRevisionSummaryResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("revisionNumber")] public int RevisionNumber { get; set; }
    /// <summary><c>draftSave</c> | <c>publish</c> | <c>rollback</c> | <c>reset</c>.</summary>
    [JsonPropertyName("revisionType")] public string RevisionType { get; set; } = "draftSave";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("createdByUserId")] public Guid? CreatedByUserId { get; set; }
    [JsonPropertyName("createdByName")] public string? CreatedByName { get; set; }
    [JsonPropertyName("sourceRevisionId")] public Guid? SourceRevisionId { get; set; }
    [JsonPropertyName("sourceRevisionNumber")] public int? SourceRevisionNumber { get; set; }
    [JsonPropertyName("isPublishedSnapshot")] public bool IsPublishedSnapshot { get; set; }
}

/// <summary>A revision plus its complete configuration snapshot.</summary>
public class StorefrontRevisionDetailResponse
{
    [JsonPropertyName("revision")] public StorefrontRevisionSummaryResponse Revision { get; set; } = new();
    [JsonPropertyName("configuration")] public StorefrontConfigDto Configuration { get; set; } = new();
}

/// <summary>One page of history, newest first.</summary>
public class StorefrontRevisionListResponse
{
    [JsonPropertyName("items")] public List<StorefrontRevisionSummaryResponse> Items { get; set; } = new();
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("pageSize")] public int PageSize { get; set; }
    [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
    [JsonPropertyName("totalPages")] public int TotalPages { get; set; }
    [JsonPropertyName("hasMore")] public bool HasMore { get; set; }

    /// <summary>The draft's current version, so the panel can flag stale state.</summary>
    [JsonPropertyName("draftVersion")] public int DraftVersion { get; set; }
}

/// <summary>
/// One meaningful difference between two revisions. <c>category</c> groups the
/// change (Hero, Theme, Sections…), <c>kind</c> says what happened and
/// <c>label</c> is the sentence the owner reads.
/// </summary>
public class StorefrontChangeResponse
{
    /// <summary><c>added</c> | <c>removed</c> | <c>modified</c> | <c>reordered</c>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "modified";
    /// <summary>Header | Hero | Theme | Typography | Sections | Pages | Footer | Media | SEO.</summary>
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    /// <summary>Human sentence, e.g. "Hero headline changed".</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    /// <summary>Optional before/after detail, e.g. "from “Hi” to “Hello”".</summary>
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    /// <summary>Stable id of the affected section/page where one exists.</summary>
    [JsonPropertyName("targetId")] public string? TargetId { get; set; }
}

/// <summary>Structured comparison between two revisions.</summary>
public class StorefrontRevisionCompareResponse
{
    [JsonPropertyName("from")] public StorefrontRevisionSummaryResponse From { get; set; } = new();
    [JsonPropertyName("to")] public StorefrontRevisionSummaryResponse To { get; set; } = new();
    [JsonPropertyName("identical")] public bool Identical { get; set; }
    [JsonPropertyName("counts")] public StorefrontChangeCountsResponse Counts { get; set; } = new();
    [JsonPropertyName("changes")] public List<StorefrontChangeResponse> Changes { get; set; } = new();
}

/// <summary>How many of each change kind the comparison found.</summary>
public class StorefrontChangeCountsResponse
{
    [JsonPropertyName("added")] public int Added { get; set; }
    [JsonPropertyName("removed")] public int Removed { get; set; }
    [JsonPropertyName("modified")] public int Modified { get; set; }
    [JsonPropertyName("reordered")] public int Reordered { get; set; }
}

/// <summary>
/// Rollback request. <c>Confirm</c> must be true: the REST surface refuses an
/// accidental restore, and <c>ExpectedVersion</c> is the draft version the client
/// last saw so a rollback can never silently discard newer work.
/// </summary>
public class RestoreStorefrontRevisionRequest
{
    [JsonPropertyName("confirm")] public bool Confirm { get; set; }
    [JsonPropertyName("expectedVersion")] public int? ExpectedVersion { get; set; }
}

/// <summary>The newly created rollback revision plus the resulting builder state.</summary>
public class RestoreStorefrontRevisionResponse
{
    [JsonPropertyName("revision")] public StorefrontRevisionSummaryResponse Revision { get; set; } = new();
    [JsonPropertyName("state")] public StorefrontBuilderStateResponse State { get; set; } = new();
}

/// <summary>
/// Server-side filters for the history feed. Bound from the query string, then
/// applied in SQL so a business with thousands of revisions never loads them.
/// </summary>
public class StorefrontRevisionQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;

    /// <summary><c>draftSave</c> | <c>publish</c> | <c>rollback</c> | <c>reset</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Only the configurations that were pushed live.</summary>
    public bool PublishedOnly { get; init; }

    /// <summary>Inclusive lower bound on creation time (UTC).</summary>
    public DateTime? From { get; init; }

    /// <summary>Inclusive upper bound on creation time (UTC).</summary>
    public DateTime? To { get; init; }

    /// <summary>Free-text match against the description or the actor's name.</summary>
    public string? Search { get; init; }
}
