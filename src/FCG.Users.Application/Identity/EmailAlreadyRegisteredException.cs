namespace FCG.Users.Application.Identity;

public sealed class EmailAlreadyRegisteredException(Exception innerException)
    : Exception("The normalized email is already registered.", innerException);
