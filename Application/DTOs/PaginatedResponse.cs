using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs
{
    /// <summary>
    /// Paginated response wrapper.
    /// </summary>
    /// <typeparam name="T">The type of items in the response.</typeparam>
    public class PaginatedResponse<T>
    {
        /// <summary>
        /// Gets or sets the items in the response.
        /// </summary>
        [JsonPropertyName("items")]
        public List<T> Items { get; set; } = [];

        /// <summary>
        /// Gets or sets the total count of items.
        /// </summary>
        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }

        /// <summary>
        /// Gets or sets the current page number.
        /// </summary>
        [JsonPropertyName("page")]
        public int Page { get; set; } = 1;

        /// <summary>
        /// Gets or sets the number of items per page.
        /// </summary>
        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; } = 20;

        /// <summary>
        /// Gets the total number of pages.
        /// </summary>
        [JsonPropertyName("totalPages")]
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    }
}