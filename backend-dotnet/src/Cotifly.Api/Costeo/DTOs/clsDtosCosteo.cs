using System.Text.Json.Serialization;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Api.Costeo.DTOs;

/// <summary>
/// <c>SolicitudCosteoWeb</c> (snake_case por <see cref="Comun.clsJsonSnakeCaseAttribute"/>): el formulario ampliado de
/// <c>POST /web/solicitudes-costeo</c>. Los límites los valida el dominio (<see cref="clsSolicitudCosteo.Validar"/>), no MVC, para
/// responder <c>SOLICITUD_COSTEO_INVALIDA</c> con el campo exacto.
/// </summary>
public sealed class clsSolicitudCosteoWebRequest
{
    public string? Tipo { get; init; }
    public string? NitCliente { get; init; }
    public string? ClienteNombre { get; init; }
    public string? LineaNegocio { get; init; }
    /// <summary>ISO 8601; sin zona se interpreta en hora de Bogotá (G4, <see cref="Comun.clsConvertidorFechaBogota"/>).</summary>
    [JsonConverter(typeof(Comun.clsConvertidorFechaBogota))]
    public DateTimeOffset? Deadline { get; init; }
    public decimal? ValorEstimado { get; init; }
    public string? Referencia { get; init; }
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }
    public string? Notas { get; init; }
    public string? Origen { get; init; }
    public string? Destino { get; init; }
    public int? OrigenMunicipioId { get; init; }
    public int? DestinoMunicipioId { get; init; }
    public string? TipoVehiculo { get; init; }
    public string? Carroceria { get; init; }
    public string? Tipologia { get; init; }
    public string? ClaseViaje { get; init; }
    public string? ClaseContenedor { get; init; }
    public bool? Exclusivo { get; init; }
    public bool? Consolidado { get; init; }
    public bool? RetornaContenedor { get; init; }
    public string? CiudadDevolucionContenedor { get; init; }
    public bool? LevanteContenedorDiferente { get; init; }
    public string? CiudadLevanteContenedor { get; init; }
    public decimal? ValorAprovisionamiento { get; init; }
    public decimal? PesoKg { get; init; }
    public decimal? ValorMercancia { get; init; }
    public IReadOnlyList<clsPosicionCargaWebRequest>? Posiciones { get; init; }

    /// <summary><c>COP</c> (por defecto) o <c>USD</c> (HU-013.7, ADR-014 §5); otro valor lo rechaza el caso de uso (<c>SOLICITUD_COSTEO_INVALIDA</c>).</summary>
    public string? Moneda { get; init; }

    /// <summary>Entrada del caso de uso, o <c>SOLICITUD_COSTEO_INVALIDA</c> si falta el tipo o no es <c>spot</c>/<c>licitacion</c>.</summary>
    public clsResultado<clsEntradaSolicitudCosteo> AEntrada()
    {
        if (!clsCodigoEnum.TryDesdeCodigo<TipoSolicitud>(Tipo, out var tipo))
        {
            return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida, "El campo 'tipo' es obligatorio y debe ser 'spot' o 'licitacion'.");
        }

        var solicitud = new clsSolicitudCosteo
        {
            Tipo = tipo,
            Deadline = Deadline,
            ValorEstimado = ValorEstimado,
            Referencia = Referencia,
            HorasCargue = HorasCargue,
            HorasDescargue = HorasDescargue,
            Notas = Notas,
            Origen = Origen ?? string.Empty,
            Destino = Destino ?? string.Empty,
            OrigenMunicipioId = OrigenMunicipioId,
            DestinoMunicipioId = DestinoMunicipioId,
            TipoVehiculo = TipoVehiculo,
            Carroceria = Carroceria,
            Tipologia = Tipologia,
            ClaseViaje = ClaseViaje,
            ClaseContenedor = ClaseContenedor,
            Exclusivo = Exclusivo,
            Consolidado = Consolidado,
            RetornaContenedor = RetornaContenedor,
            CiudadDevolucionContenedor = CiudadDevolucionContenedor,
            LevanteContenedorDiferente = LevanteContenedorDiferente,
            CiudadLevanteContenedor = CiudadLevanteContenedor,
            ValorAprovisionamiento = ValorAprovisionamiento,
            PesoKg = PesoKg,
            ValorMercancia = ValorMercancia,
            Posiciones = [.. (Posiciones ?? []).Select(p => p is null
                ? null!
                : new clsPosicionCarga(p.Descripcion, p.Cantidad, p.LargoCm, p.AnchoCm, p.AltoCm, p.PesoKg))],
        };
        return new clsEntradaSolicitudCosteo(NitCliente, ClienteNombre, LineaNegocio, solicitud, Moneda);
    }
}

/// <summary><c>PosicionCargaWeb</c>: un bulto en centímetros y kilogramos; <c>cantidad</c> por defecto 1.</summary>
public sealed class clsPosicionCargaWebRequest
{
    public string? Descripcion { get; init; }
    public int Cantidad { get; init; } = 1;
    public decimal LargoCm { get; init; }
    public decimal AnchoCm { get; init; }
    public decimal AltoCm { get; init; }
    public decimal? PesoKg { get; init; }
}

/// <summary><c>SeleccionServiciosConexos</c>: ids de los servicios conexos del costeo que van en el PDF (los demás se excluyen).</summary>
public sealed record clsSeleccionServiciosConexosWebRequest(IReadOnlyList<string>? Incluidos);

/// <summary>
/// <c>AjusteMargenWeb</c> (HU-013.2): <c>margen</c> (fracción de 0 a 0,99) y <c>justificacion</c> (obligatoria por debajo del margen
/// sugerido). Los límites los valida el dominio para responder <c>AJUSTE_MARGEN_INVALIDO</c> o <c>JUSTIFICACION_REQUERIDA</c>.
/// </summary>
public sealed record clsAjusteMargenWebRequest(decimal? Margen, string? Justificacion);

/// <summary><c>ResultadoWeb</c>: <c>resultado</c> (<c>ganada</c> | <c>perdida</c>), <c>motivo</c> (se guarda en <c>motivo_rechazo</c>) y
/// <c>comentario</c> (solo se reenvía a Nexo).</summary>
public sealed record clsResultadoWebRequest(string? Resultado, string? Motivo, string? Comentario);
