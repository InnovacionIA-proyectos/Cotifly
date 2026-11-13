using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Cubicacion.DTOs;

/// <summary>Estados de una cubicación rápida en segundo plano (<c>GET /web/cubicacion/{id}</c>).</summary>
public static class clsEstadosCubicacionRapida
{
    /// <summary>El cubicador sigue calculando (o la llamada espera su cupo).</summary>
    public const string EnProceso = "en_proceso";

    /// <summary>Terminó: trae el resultado (la misma forma del 200 de <c>POST /web/cubicacion</c>).</summary>
    public const string Completada = "completada";

    /// <summary>Terminó con error: trae <c>{codigo, mensaje}</c> (por ejemplo un 400 del cubicador o <c>CUBICADOR_NO_DISPONIBLE</c>).</summary>
    public const string Fallida = "fallida";
}

/// <summary>
/// Una cubicación rápida que siguió en segundo plano. <paramref name="UsuarioId"/> es el dueño: solo él la puede consultar.
/// <see cref="Resultado"/> va con <see cref="clsEstadosCubicacionRapida.Completada"/> y <see cref="Error"/> con
/// <see cref="clsEstadosCubicacionRapida.Fallida"/>.
/// </summary>
public sealed record clsCubicacionRapida(Guid Id, Guid UsuarioId, string Estado, DateTimeOffset CreadaUtc, DateTimeOffset ExpiraUtc)
{
    public clsResultadoCubicacion? Resultado { get; init; }

    public clsErrorDominio? Error { get; init; }

    public bool Terminada => Estado != clsEstadosCubicacionRapida.EnProceso;
}

/// <summary>
/// Cómo terminó <c>POST /web/cubicacion</c>. Con <see cref="Resultado"/>, el cubicador respondió dentro de la espera síncrona
/// (200); con <see cref="IdPendiente"/>, el cálculo sigue en segundo plano (202 y <c>GET /web/cubicacion/{id}</c>).
/// </summary>
public sealed record clsInicioCubicacionRapida(clsResultadoCubicacion? Resultado, Guid? IdPendiente);
