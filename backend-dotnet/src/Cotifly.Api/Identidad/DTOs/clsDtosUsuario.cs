namespace Cotifly.Api.Identidad.DTOs;

/// <summary><c>TelefonoComercial</c> de entrada: <c>{"telefono": "+57 300 123 4567"}</c>; <c>null</c> o vacío lo borra.</summary>
public sealed record clsTelefonoRequest(string? Telefono);
