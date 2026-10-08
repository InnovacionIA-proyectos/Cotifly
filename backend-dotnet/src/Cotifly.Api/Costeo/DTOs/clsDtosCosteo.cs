using System.Text.Json;
using System.Text.Json.Serialization;
using Cotifly.Api.Comun;
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
    /// <summary>Obsoleto (decisión 1 de HU-021): se acepta y se ignora; deja de enviarse a Nexo. Lo retira el contrato de #138.</summary>
    public decimal? ValorEstimado { get; init; }
    /// <summary>Obsoleto (decisión 1 de HU-021): se acepta y se ignora. Lo retira el contrato de #138.</summary>
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

/// <summary>
/// <c>SeleccionServiciosConexos</c> (HU-005.7; por ruta desde HU-021.8, #135): <c>conexos</c> (los que quedan en la ruta, con su cantidad;
/// los demás se quitan) o <c>incluidos</c> (los ids que se conservan; los demás se quitan), y la ruta (<c>linea_orden</c> o
/// <c>linea_solicitud_id</c>). Ver <see cref="clsCambioServiciosConexos"/>.
/// </summary>
public sealed record clsSeleccionServiciosConexosWebRequest(
    IReadOnlyList<string>? Incluidos,
    int? LineaOrden = null,
    Guid? LineaSolicitudId = null,
    IReadOnlyList<clsCantidadConexoWebRequest>? Conexos = null)
{
    /// <summary>Entrada del caso de uso.</summary>
    public clsCambioServiciosConexos AEntrada() => new(
        new clsRutaIndicada(LineaOrden, LineaSolicitudId),
        Incluidos,
        Conexos is null ? null : [.. Conexos.Select(c => c is null ? null! : new clsCantidadConexoEntrada(c.Id, c.Cantidad))]);
}

/// <summary><c>CantidadConexoWeb</c>: <c>id</c> de un conexo de la ruta (<c>servicios_conexos[].id</c>) y su <c>cantidad</c> nueva.</summary>
public sealed record clsCantidadConexoWebRequest(string? Id, decimal? Cantidad);

/// <summary>
/// <c>AjusteMargenWeb</c> (HU-013.2; por ruta desde HU-021.8, #135): <c>margen</c> (fracción de 0 a 0,99), <c>justificacion</c>
/// (obligatoria por debajo del margen sugerido de la ruta) y la ruta (<c>linea_orden</c> o <c>linea_solicitud_id</c>; opcional si la
/// cotización tiene una sola). Los límites los valida el dominio para responder <c>AJUSTE_MARGEN_INVALIDO</c>,
/// <c>JUSTIFICACION_REQUERIDA</c>, <c>RUTA_REQUERIDA</c> o <c>RUTA_INEXISTENTE</c>.
/// </summary>
public sealed record clsAjusteMargenWebRequest(decimal? Margen, string? Justificacion, int? LineaOrden = null, Guid? LineaSolicitudId = null);

/// <summary><c>ResultadoWeb</c>: <c>resultado</c> (<c>ganada</c> | <c>perdida</c>), <c>motivo</c> (se guarda en <c>motivo_rechazo</c>) y
/// <c>comentario</c> (solo se reenvía a Nexo).</summary>
public sealed record clsResultadoWebRequest(string? Resultado, string? Motivo, string? Comentario);

/// <summary>
/// <c>ReSolicitudCosteoWeb</c> (HU-013.4): <c>motivo</c>, <c>datos_carga</c> (<c>DatosCargaWeb</c>; ausente o <c>null</c> = sin cambios) y
/// <c>nueva_version</c>. <c>datos_carga</c> se lee como JSON para distinguir un campo que no viene (se conserva) de uno en <c>null</c>
/// (se borra); los límites y la coherencia los valida el dominio con las reglas de la captura.
/// </summary>
public sealed record clsReSolicitudCosteoWebRequest(string? Motivo, JsonElement? DatosCarga, bool? NuevaVersion)
{
    /// <summary>
    /// Entrada del caso de uso, o <c>SOLICITUD_COSTEO_INVALIDA</c> si <c>datos_carga</c> no es un objeto o trae un campo desconocido o
    /// de otro tipo.
    /// </summary>
    public clsResultado<clsEntradaReSolicitudCosteo> AEntrada()
    {
        if (DatosCarga is not { } datos || datos.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new clsEntradaReSolicitudCosteo(Motivo, null, NuevaVersion ?? false);
        }

        var cambios = clsDatosCargaWebRequest.ACambios(datos);
        return cambios.EsFallo ? cambios.Error : new clsEntradaReSolicitudCosteo(Motivo, cambios.Valor, NuevaVersion ?? false);
    }
}

/// <summary>
/// <c>DatosCargaWeb</c> (HU-013.4): los mismos campos de línea que la captura (más las horas de cargue y descargue), solo los que
/// cambian. Origen, destino, cliente y línea de negocio no se cambian por esta vía.
/// </summary>
public sealed class clsDatosCargaWebRequest
{
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
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }
    public decimal? PesoKg { get; init; }
    public decimal? ValorMercancia { get; init; }
    public IReadOnlyList<clsPosicionCargaWebRequest>? Posiciones { get; init; }

    /// <summary>Cambios de carga del dominio a partir del JSON de <c>datos_carga</c> (nombres snake_case del contrato).</summary>
    public static clsResultado<clsCambiosCarga> ACambios(JsonElement datos)
    {
        if (datos.ValueKind != JsonValueKind.Object)
        {
            return Invalido("El campo 'datos_carga' debe ser un objeto con los campos de carga que cambian.");
        }

        var presentes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propiedad in datos.EnumerateObject())
        {
            var nombre = propiedad.Name.ToLowerInvariant();
            if (!clsCambiosCarga.Campos.Contains(nombre))
            {
                return Invalido($"El campo 'datos_carga.{propiedad.Name}' no se puede cambiar al re-solicitar (origen, destino, cliente y "
                    + "línea de negocio son otra cotización).");
            }

            presentes.Add(nombre);
        }

        clsDatosCargaWebRequest? leido;
        try
        {
            leido = datos.Deserialize<clsDatosCargaWebRequest>(clsJsonWeb.Opciones);
        }
        catch (JsonException ex)
        {
            return Invalido($"El campo 'datos_carga' tiene un valor de tipo inválido{(ex.Path is { } ruta ? $" en '{ruta.TrimStart('$', '.')}'" : string.Empty)}.");
        }

        if (leido is null)
        {
            return Invalido("El campo 'datos_carga' debe ser un objeto con los campos de carga que cambian.");
        }

        return new clsCambiosCarga
        {
            Presentes = presentes,
            TipoVehiculo = leido.TipoVehiculo,
            Carroceria = leido.Carroceria,
            Tipologia = leido.Tipologia,
            ClaseViaje = leido.ClaseViaje,
            ClaseContenedor = leido.ClaseContenedor,
            Exclusivo = leido.Exclusivo,
            Consolidado = leido.Consolidado,
            RetornaContenedor = leido.RetornaContenedor,
            CiudadDevolucionContenedor = leido.CiudadDevolucionContenedor,
            LevanteContenedorDiferente = leido.LevanteContenedorDiferente,
            CiudadLevanteContenedor = leido.CiudadLevanteContenedor,
            ValorAprovisionamiento = leido.ValorAprovisionamiento,
            HorasCargue = leido.HorasCargue,
            HorasDescargue = leido.HorasDescargue,
            PesoKg = leido.PesoKg,
            ValorMercancia = leido.ValorMercancia,
            Posiciones = leido.Posiciones is null
                ? null
                : [.. leido.Posiciones.Select(p => p is null
                    ? null!
                    : new clsPosicionCarga(p.Descripcion, p.Cantidad, p.LargoCm, p.AnchoCm, p.AltoCm, p.PesoKg))],
        };
    }

    private static clsErrorDominio Invalido(string mensaje) => new(clsCodigosError.SolicitudCosteoInvalida, mensaje);
}
