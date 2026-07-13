using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IBlogGenerationService
    {
        /// <summary>Generates a PendingReview draft from a queued topic and marks the topic Generated.</summary>
        Task<BlogPost> GenerateFromTopicAsync(BlogTopic topic, CancellationToken cancellationToken = default);

        /// <summary>Generates a PendingReview draft from a freeform topic (manual trigger only).</summary>
        Task<BlogPost> GenerateAsync(string topicTitle, string? targetKeyword, string? notes, CancellationToken cancellationToken = default);

        /// <summary>Asks Claude for 15–20 topic ideas that don't duplicate existing posts/topics.</summary>
        Task<List<SuggestedTopicDto>> SuggestTopicsAsync(CancellationToken cancellationToken = default);
    }
}
