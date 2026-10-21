using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs
{
    /// <summary>
    /// Standard API response wrapper matching the spec format.
    /// All endpoints return this structure for consistency.
    /// </summary>
    /// <typeparam name="T">Type of the data payload.</typeparam>
    public class ApiResponse<T>
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public T? Data { get; set; }

        [JsonPropertyName("error")]
        public ApiError? Error { get; set; }

        /// <summary>Creates a successful response.</summary>
        public static ApiResponse<T> Ok(T data) => new()
        {
            Success = true,
            Data = data,
            Error = null
        };

        /// <summary>Creates a failure response with an error.</summary>
        public static ApiResponse<T> Fail(string code, string message) => new()
        {
            Success = false,
            Data = default,
            Error = new ApiError { Code = code, Message = message }
        };
    }
}