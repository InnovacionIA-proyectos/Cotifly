using Cotifly.Dominio.Comun;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>
/// Opciones del resultado ganada/perdida (sección <see cref="Seccion"/>). La lista de motivos de pérdida es
/// configurable; sin configurar es la de NEXO (<c>mesa-costeo-page.component.ts</c>): <see cref="MotivosPorDefecto"/>.
/// </summary>
public sealed class clsOpcionesResultado
{
    public const string Seccion = "Resultado";

    /// <summary>Largo máximo de un motivo de la lista: el de <c>motivo</c> en Nexo (<c>POST /api/cotizaciones/{id}/resultado</c>).</summary>
    public const int LargoMaximoMotivo = 100;

    /// <summary>La lista de NEXO, en su orden.</summary>
    public static readonly IReadOnlyList<string> MotivosPorDefecto =
    [
        "Valor muy alto",
        "Flete fuera de rango de plaza",
        "Cliente desistió del servicio",
        "Tiempo de respuesta vencido",
        "Otro",
    ];

    /// <summary>El motivo de la lista que exige un comentario, por defecto <c>Otro</c>.</summary>
    public const string MotivoConComentarioPorDefecto = "Otro";

    /// <summary>
    /// <c>Resultado__MotivosPerdida__0</c>, <c>__1</c>... La lista configurada REEMPLAZA a la de por defecto (los vacíos se ignoran; todos
    /// vacíos = la de por defecto). Cada motivo tiene máximo <see cref="LargoMaximoMotivo"/> caracteres (si no, no arranca).
    /// </summary>
    /// <remarks>
    /// Sin valor inicial a propósito: el enlazador de configuración AGREGA los elementos configurados a una lista que ya tiene valores, así
    /// que un valor por defecto en la lista no se podría reemplazar.
    /// </remarks>
    public List<string>? MotivosPerdida { get; set; }

    /// <summary>
    /// <c>Resultado__MotivoConComentario</c>: el motivo que exige <c>comentario</c> (por defecto <c>Otro</c>). Debe estar en la lista (si no,
    /// no arranca).
    /// </summary>
    public string? MotivoConComentario { get; set; }

    /// <summary>Los motivos vigentes: los configurados (recortados, sin vacíos ni repetidos) o la lista de NEXO.</summary>
    public IReadOnlyList<string> Motivos =>
        (MotivosPerdida ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim().Normalize(System.Text.NormalizationForm.FormC))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            is { Count: > 0 } configurados ? configurados : MotivosPorDefecto;

    /// <summary>El motivo que exige comentario, recortado.</summary>
    public string ConComentario => string.IsNullOrWhiteSpace(MotivoConComentario)
        ? MotivoConComentarioPorDefecto
        : MotivoConComentario.Trim().Normalize(System.Text.NormalizationForm.FormC);

    /// <summary>Los errores de la configuración (vacío si es válida). Los valida <c>clsValidadorOpcionesResultado</c> al arrancar.</summary>
    public IReadOnlyList<string> Errores()
    {
        var errores = Motivos.Where(m => m.Length > LargoMaximoMotivo)
            .Select(m => $"Resultado:MotivosPerdida: el motivo '{m}' supera {LargoMaximoMotivo} caracteres (límite de Nexo).")
            .ToList();
        if (!Motivos.Contains(ConComentario, StringComparer.OrdinalIgnoreCase))
        {
            errores.Add($"Resultado:MotivoConComentario: '{ConComentario}' no está en Resultado:MotivosPerdida.");
        }

        return errores;
    }
}

/// <summary>Un motivo de pérdida de <c>GET /web/motivos-perdida</c> (<c>MotivoPerdida</c>).</summary>
/// <param name="Motivo">El texto exacto que se envía en <c>motivo</c> (o en <c>motivo_rechazo</c> de <c>/seguimiento</c>).</param>
/// <param name="RequiereComentario">Si exige <c>comentario</c> (el motivo <c>Otro</c>).</param>
public sealed record clsMotivoPerdida(string Motivo, bool RequiereComentario);

/// <summary>Respuesta de <c>GET /web/motivos-perdida</c> (<c>MotivosPerdida</c>): la lista en su orden.</summary>
public sealed record clsMotivosPerdida(IReadOnlyList<clsMotivoPerdida> Motivos);

/// <summary>
/// Un motivo de pérdida válido. <see cref="Motivo"/> es el de la lista (con su escritura) y va a Nexo en <c>motivo</c>;
/// <see cref="Comentario"/> va a Nexo en <c>comentario</c>; <see cref="ParaCotifly"/> es lo que se guarda en <c>motivoRechazo</c>.
/// </summary>
public sealed record clsMotivoPerdidaValidado(string Motivo, string? Comentario, string ParaCotifly);

/// <summary>
/// La lista de motivos de pérdida y su validación, común a <c>POST .../resultado</c> y a
/// <c>POST .../seguimiento</c>. Solo valida lo nuevo: los resultados ya guardados (motivo libre o sin motivo) no se revalidan.
/// </summary>
public interface IClsCatalogoMotivosPerdida
{
    /// <summary>La lista vigente, en su orden, con el motivo que exige comentario.</summary>
    clsMotivosPerdida Listar();

    /// <summary>
    /// Valida el motivo de una perdida: obligatorio y de la lista (sin distinguir mayúsculas ni espacios de los extremos; se devuelve con
    /// la escritura de la lista). El motivo <c>Otro</c> exige <paramref name="comentario"/>. Error: <c>MOTIVO_NO_PERMITIDO</c> (400) con
    /// <c>detalle.motivos_permitidos</c> y <c>detalle.requiere_comentario</c>.
    /// </summary>
    clsResultado<clsMotivoPerdidaValidado> Validar(string? motivo, string? comentario);

    /// <summary>
    /// El <c>motivo</c> que va a Nexo a partir del <c>motivoRechazo</c> guardado (la sincronización diferida): con el motivo
    /// <c>Otro</c> se guarda <c>"Otro: comentario"</c> y a Nexo va solo <c>Otro</c> (el comentario va aparte). Otro valor sale igual.
    /// </summary>
    string? MotivoParaNexo(string? guardado);
}

/// <summary>Implementación de <see cref="IClsCatalogoMotivosPerdida"/> sobre <see cref="clsOpcionesResultado"/>.</summary>
public sealed class clsCatalogoMotivosPerdida(IOptions<clsOpcionesResultado> opciones) : IClsCatalogoMotivosPerdida
{
    /// <summary>Separador entre el motivo <c>Otro</c> y su comentario en <c>motivoRechazo</c>: <c>"Otro: el cliente..."</c>.</summary>
    public const string Separador = ": ";

    /// <summary>La lista de NEXO sin configurar (pruebas y servicios construidos a mano).</summary>
    public static clsCatalogoMotivosPerdida PorDefecto { get; } = new(Options.Create(new clsOpcionesResultado()));

    private readonly clsOpcionesResultado _opciones = (opciones ?? throw new ArgumentNullException(nameof(opciones))).Value;

    public clsMotivosPerdida Listar()
    {
        var conComentario = _opciones.ConComentario;
        return new clsMotivosPerdida(
            [.. _opciones.Motivos.Select(m => new clsMotivoPerdida(m, string.Equals(m, conComentario, StringComparison.OrdinalIgnoreCase)))]);
    }

    public clsResultado<clsMotivoPerdidaValidado> Validar(string? motivo, string? comentario)
    {
        var limpio = Normalizar(motivo);
        var deLaLista = limpio is null ? null : _opciones.Motivos.FirstOrDefault(m => string.Equals(m, limpio, StringComparison.OrdinalIgnoreCase));
        if (deLaLista is null)
        {
            return Error(
                limpio is null
                    ? "Para marcar la cotización como perdida, el motivo es obligatorio: elige uno de la lista."
                    : $"El motivo '{limpio}' no está en la lista de motivos de pérdida: elige uno de la lista.",
                requiereComentario: false);
        }

        var comentarioLimpio = Normalizar(comentario);
        var exigeComentario = string.Equals(deLaLista, _opciones.ConComentario, StringComparison.OrdinalIgnoreCase);
        if (exigeComentario && comentarioLimpio is null)
        {
            return Error($"Con el motivo '{deLaLista}' el comentario es obligatorio: explica por qué se perdió.", requiereComentario: true);
        }

        return new clsMotivoPerdidaValidado(
            deLaLista, comentarioLimpio, exigeComentario ? deLaLista + Separador + comentarioLimpio : deLaLista);
    }

    public string? MotivoParaNexo(string? guardado)
    {
        var conComentario = _opciones.ConComentario;
        // Con la escritura de la LISTA (la que espera Nexo y la del envío directo), no la de Resultado:MotivoConComentario, que se compara
        // sin distinguir mayúsculas: con MotivoConComentario = "otro" y la lista "Otro", a Nexo va "Otro".
        return guardado is not null && guardado.StartsWith(conComentario + Separador, StringComparison.OrdinalIgnoreCase)
            ? _opciones.Motivos.FirstOrDefault(m => string.Equals(m, conComentario, StringComparison.OrdinalIgnoreCase)) ?? conComentario
            : guardado;
    }

    private clsErrorDominio Error(string mensaje, bool requiereComentario) => new(clsCodigosError.MotivoNoPermitido, mensaje)
    {
        Datos = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["motivos_permitidos"] = _opciones.Motivos.ToArray(),
            ["requiere_comentario"] = requiereComentario,
        },
    };

    /// <summary>
    /// Recorta y lleva a la forma Unicode compuesta (NFC): "desistió" escrito con la tilde aparte (NFD, p. ej. desde macOS) es el mismo
    /// motivo de la lista y a Nexo viaja con la escritura de la lista.
    /// </summary>
    private static string? Normalizar(string? texto) =>
        string.IsNullOrWhiteSpace(texto) ? null : texto.Trim().Normalize(System.Text.NormalizationForm.FormC);
}
