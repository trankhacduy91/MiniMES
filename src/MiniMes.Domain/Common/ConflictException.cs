namespace MiniMes.Domain.Common;

// Duplicate code or concurrency conflict; the API maps it to 409 ("reload and try again").
public class ConflictException(string message) : Exception(message);
