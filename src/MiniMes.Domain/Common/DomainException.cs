namespace MiniMes.Domain.Common;

// Thrown when a business rule is violated; the API maps it to 422.
// A blocked scan is NOT an exception: it is a normal result with a reasonCode.
public class DomainException(string message) : Exception(message);
