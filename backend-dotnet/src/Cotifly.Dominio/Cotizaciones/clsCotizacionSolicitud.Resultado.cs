using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// HU-033 (#199, ADR-023, script 13): resultado ganada/perdida diferido a Nexo. Si el comercial marca el resultado cuando la cotización
/// todavía no está Entregada en Nexo (Nexo responde <c>COTIZACION_NO_ENTREGADA</c>), el resultado se guarda SOLO en Cotifly como
/// <see cref="SincronizacionResultadoNexo.Pendiente"/> y el polling del costeo lo envía a Nexo UNA vez cuando ve la entrega. Cotifly
/// nunca entrega la cotización en Nexo (no llama a <c>/entregar</c>: eso es de la Mesa de Costeo).
/// </summary>
public sealed partial class clsCotizacionSolicitud
{
    /// <summary>Largo máximo del comentario pendiente (el de Nexo: 500).</summary>
    public const int LargoMaximoComentarioResultado = 500;

    /// <summary>Largo máximo del error de la sincronización.</summary>
    public const int LargoMaximoErrorSincronizacion = 1000;

    /// <summary>Estado guardado de la sincronización del resultado con Nexo (<c>null</c>: sin resultado o anterior al script 13).</summary>
    public SincronizacionResultadoNexo? SincronizacionResultado { get; private set; }

    /// <summary>
    /// Comentario del comercial para Nexo mientras el resultado está pendiente (Cotifly no persiste el comentario de un resultado que ya
    /// fue a Nexo, HU-005.14): se borra al sincronizar.
    /// </summary>
    public string? ComentarioResultadoPendiente { get; private set; }

    /// <summary>Cuándo Nexo aceptó el resultado (en el momento o desde la cola de pendientes).</summary>
    public DateTimeOffset? ResultadoSincronizadoAt { get; private set; }

    /// <summary>Último error al enviar el resultado pendiente, o por qué ya no se puede enviar.</summary>
    public string? ErrorSincronizacionResultado { get; private set; }

    /// <summary>Envíos fallidos seguidos del resultado pendiente (tope <c>CosteoNexo:MaximoFallosSincronizacionResultado</c>).</summary>
    public int IntentosSincronizacionResultado { get; private set; }

    /// <summary>¿Hay un resultado guardado solo en Cotifly esperando a que Nexo entregue la cotización?</summary>
    public bool ResultadoPendienteDeSincronizar => Ganada is not null && SincronizacionResultado == SincronizacionResultadoNexo.Pendiente;

    /// <summary>
    /// Lo que ve el frontend (<c>sincronizacion_resultado_nexo</c>): <c>null</c> sin resultado; <c>solo_cotifly</c> si el resultado nunca
    /// va a Nexo (seguimiento de una cotización sin Nexo, descartada o vencida, o la perdida antes del costeo de HU-013.17);
    /// <c>pendiente</c>, <c>sincronizado</c> o <c>no_sincronizable</c> según la columna; y <c>sincronizado</c> para un resultado anterior
    /// al script 13 marcado en Nexo.
    /// </summary>
    public string? EstadoSincronizacionResultado =>
        Ganada is null ? null
        : SincronizacionResultado is { } guardado ? clsCodigoEnum.ACodigo(guardado)
        : PerdidaSoloEnCotifly || ResultadoSoloLocal || SolicitudNexoId is null ? CodigoSoloCotifly
        : clsCodigoEnum.ACodigo(SincronizacionResultadoNexo.Sincronizado);

    /// <summary>Código de <see cref="EstadoSincronizacionResultado"/> de un resultado que nunca va a Nexo.</summary>
    public const string CodigoSoloCotifly = "solo_cotifly";

    /// <summary>
    /// Guarda el resultado solo en Cotifly, pendiente de enviarlo a Nexo cuando la Mesa entregue la cotización. No cambia el estado de
    /// costeo ni la agenda del polling (lo sigue consultando). El comentario se guarda (recortado a 500) para enviarlo después.
    /// </summary>
    public void RegistrarResultadoPendienteNexo(bool ganada, string? motivo, string? comentario, DateTimeOffset cuando)
    {
        MarcarSeguimiento(ganada, motivo, cuando);
        SincronizacionResultado = SincronizacionResultadoNexo.Pendiente;
        var limpio = clsValidacion.Normalizar(comentario);
        ComentarioResultadoPendiente = limpio is { Length: > LargoMaximoComentarioResultado } ? limpio[..LargoMaximoComentarioResultado] : limpio;
        ResultadoSincronizadoAt = null;
        ErrorSincronizacionResultado = null;
        IntentosSincronizacionResultado = 0;
    }

    /// <summary>Nexo aceptó el resultado: queda sincronizado, sin comentario pendiente ni error (salvo <paramref name="nota"/>).</summary>
    public void MarcarResultadoSincronizado(DateTimeOffset cuando, string? nota = null)
    {
        SincronizacionResultado = SincronizacionResultadoNexo.Sincronizado;
        ResultadoSincronizadoAt = cuando;
        ComentarioResultadoPendiente = null;
        ErrorSincronizacionResultado = RecortarError(nota);
        IntentosSincronizacionResultado = 0;
    }

    /// <summary>El envío del resultado pendiente falló (Nexo caído o con error): suma un intento y guarda el error; sigue pendiente.</summary>
    public void RegistrarFalloSincronizacionResultado(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        IntentosSincronizacionResultado++;
        ErrorSincronizacionResultado = RecortarError(error);
    }

    /// <summary>El resultado pendiente ya no se puede enviar a Nexo: queda solo en Cotifly con el motivo.</summary>
    public void MarcarResultadoNoSincronizable(string motivo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        SincronizacionResultado = SincronizacionResultadoNexo.NoSincronizable;
        ErrorSincronizacionResultado = RecortarError(motivo);
    }

    private static string? RecortarError(string? texto)
    {
        var limpio = clsValidacion.Normalizar(texto);
        return limpio is { Length: > LargoMaximoErrorSincronizacion } ? limpio[..(LargoMaximoErrorSincronizacion - 1)] + "…" : limpio;
    }
}
