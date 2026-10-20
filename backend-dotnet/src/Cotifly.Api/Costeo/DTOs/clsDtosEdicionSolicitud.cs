using System.Text.Json.Serialization;
using Cotifly.Aplicacion.Costeo.DTOs;

namespace Cotifly.Api.Costeo.DTOs;

/// <summary>
/// HU-028 (#186): <c>EdicionSolicitudWeb</c>, el cuerpo de <c>PUT /web/cotizaciones/{folio}/solicitud</c>. Reemplazo completo del contenido
/// editable con la forma de la captura (<see cref="clsSolicitudCosteoWebRequest"/>) más el id de cada ruta que se conserva. Los campos
/// bloqueados (<c>tipo</c>, <c>nit_cliente</c>, <c>cliente_nombre</c>, <c>origen_cliente</c>) son opcionales y, si vienen, deben traer el
/// valor guardado. Los límites los valida el caso de uso (todos los errores en <c>detalle.errores</c>).
/// </summary>
public sealed class clsEdicionSolicitudWebRequest
{
    public string? Tipo { get; init; }
    public string? NitCliente { get; init; }
    public string? ClienteNombre { get; init; }
    public string? OrigenCliente { get; init; }

    /// <summary>Línea de negocio por defecto de una ruta nueva (una de la solicitud).</summary>
    public string? LineaNegocio { get; init; }

    /// <summary>ISO 8601; sin zona se interpreta en hora de Bogotá (G4).</summary>
    [JsonConverter(typeof(Comun.clsConvertidorFechaBogota))]
    public DateTimeOffset? Deadline { get; init; }
    public string? Notas { get; init; }
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }

    /// <summary><c>COP</c> o <c>USD</c>; ausente conserva la moneda guardada.</summary>
    public string? Moneda { get; init; }

    /// <summary>Por qué se edita (opcional, hasta 300): auditoría y comentario del rechazo en Nexo.</summary>
    public string? Motivo { get; init; }

    /// <summary>El modo que anunció el GET (<c>en_sitio</c>, <c>reemplazo</c> o <c>nueva_version</c>); si cambió, 409.</summary>
    public string? ModoEsperado { get; init; }

    public IReadOnlyList<clsRutaEdicionWebRequest?>? Rutas { get; init; }

    /// <summary>Entrada del caso de uso.</summary>
    public clsEntradaEdicionSolicitud AEntrada() => new(
        [.. (Rutas ?? []).Select(r => r is null ? null : new clsRutaEdicionEntrada(r.LineaSolicitudId, r.AEntrada(), r.ReincluirRechazados ?? false))],
        Tipo, NitCliente, ClienteNombre, OrigenCliente, LineaNegocio, Deadline, Notas, HorasCargue, HorasDescargue, Moneda, Motivo, ModoEsperado);
}

/// <summary>
/// <c>RutaEdicionWeb</c> (HU-028): una ruta de la edición, con los campos de <c>RutaCapturaWeb</c> más <c>linea_solicitud_id</c> (el id de
/// la ruta guardada que se edita; sin él, una ruta nueva). La línea de negocio de una ruta guardada no cambia.
/// </summary>
public sealed class clsRutaEdicionWebRequest : clsRutaCapturaWebRequest
{
    public Guid? LineaSolicitudId { get; init; }

    /// <summary>
    /// HU-028.1 (#183): <c>true</c> = los camiones de esta ruta que rechazó la Mesa de Costeo vuelven a viajar a Nexo, sin el rechazo, para
    /// que la Mesa los reconsidere. Ausente o <c>false</c> (por defecto): siguen rechazados y no viajan.
    /// </summary>
    public bool? ReincluirRechazados { get; init; }
}
