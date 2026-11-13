using System.Text.Json;
using System.Text.Json.Serialization;
using Cotifly.Api.Comun;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Api.Costeo.DTOs;

/// <summary>
/// <c>SolicitudCosteoWeb</c> (snake_case por <see cref="Comun.clsJsonSnakeCaseAttribute"/>): el formulario de
/// <c>POST /web/solicitudes-costeo</c>. El formulario captura <see cref="Rutas"/> (<c>RutaCapturaWeb</c>); los campos de
/// ruta al nivel raíz son el formato plano obsoleto (una sola ruta) y se aceptan solo sin <c>rutas</c>. Los límites los valida
/// el dominio, no MVC, para responder <c>SOLICITUD_COSTEO_INVALIDA</c> con todos los campos en <c>detalle.errores</c>.
/// </summary>
public sealed class clsSolicitudCosteoWebRequest
{
    public string? Tipo { get; init; }
    public string? NitCliente { get; init; }
    public string? ClienteNombre { get; init; }

    /// <summary>Línea de negocio por defecto de las rutas (cada ruta puede traer la suya).</summary>
    public string? LineaNegocio { get; init; }

    /// <summary>Las rutas de la solicitud (al menos una). Sin ellas se lee el formato plano obsoleto como una sola ruta.</summary>
    public IReadOnlyList<clsRutaCapturaWebRequest?>? Rutas { get; init; }
    /// <summary>ISO 8601; sin zona se interpreta en hora de Bogotá (<see cref="Comun.clsConvertidorFechaBogota"/>).</summary>
    [JsonConverter(typeof(Comun.clsConvertidorFechaBogota))]
    public DateTimeOffset? Deadline { get; init; }
    /// <summary>
    /// Retirado del contrato: no está en <c>openapi.yaml</c>. Si un frontend anterior lo manda, se ignora
    /// sin error y queda un aviso en el log (<see cref="CamposRetirados"/>).
    /// </summary>
    [clsCampoRetirado("decisión 1 de HU-021")]
    public decimal? ValorEstimado { get; init; }

    /// <summary>Retirado del contrato: igual que <see cref="ValorEstimado"/>.</summary>
    [clsCampoRetirado("decisión 1 de HU-021")]
    public string? Referencia { get; init; }

    /// <summary>Nombres snake_case de los campos retirados que llegaron (para el aviso del log).</summary>
    public IReadOnlyList<string> CamposRetirados() =>
        [.. new[] { (ValorEstimado is not null, "valor_estimado"), (Referencia is not null, "referencia") }.Where(c => c.Item1).Select(c => c.Item2)];
    /// <summary>Horas por defecto de las rutas (cada ruta puede traer las suyas).</summary>
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }
    public string? Notas { get; init; }

    // ---- Formato plano obsoleto (una ruta): solo se lee sin 'rutas'.
    public string? Origen { get; init; }
    public string? Destino { get; init; }
    public int? OrigenMunicipioId { get; init; }
    public int? DestinoMunicipioId { get; init; }

    /// <summary>El vehículo elegido de la ruta (código de Nexo, ADR-022). Obligatorio sin mercancías cubicables, salvo en Contenedor (<c>VEHICULO_REQUERIDO</c>); es el respaldo del cubicador: si el cubicador asigna, manda el cubicador. La carrocería es opcional.</summary>
    public string? TipoVehiculo { get; init; }

    /// <summary>El vehículo elegido de la ruta (código de Nexo, ADR-022). Obligatorio sin mercancías cubicables, salvo en Contenedor (<c>VEHICULO_REQUERIDO</c>); es el respaldo del cubicador: si el cubicador asigna, manda el cubicador. La carrocería es opcional.</summary>
    public string? Carroceria { get; init; }
    public string? Tipologia { get; init; }
    public string? ClaseViaje { get; init; }
    public string? ClaseContenedor { get; init; }
    public bool? Exclusivo { get; init; }
    public bool? Consolidado { get; init; }
    public bool? RetornaContenedor { get; init; }
    public string? CiudadDevolucionContenedor { get; init; }
    public int? CiudadDevolucionMunicipioId { get; init; }
    public bool? LevanteContenedorDiferente { get; init; }
    public string? CiudadLevanteContenedor { get; init; }
    public int? CiudadLevanteMunicipioId { get; init; }
    public decimal? ValorAprovisionamiento { get; init; }
    public decimal? PesoKg { get; init; }
    public decimal? ValorMercancia { get; init; }
    public IReadOnlyList<clsPosicionCargaWebRequest?>? Posiciones { get; init; }

    /// <summary><c>COP</c> (por defecto) o <c>USD</c> (ADR-014 §5); otro valor lo rechaza el caso de uso (<c>SOLICITUD_COSTEO_INVALIDA</c>).</summary>
    public string? Moneda { get; init; }

    /// <summary>
    /// Entrada del caso de uso, o <c>SOLICITUD_COSTEO_INVALIDA</c> si falta el tipo o no es <c>spot</c>/<c>licitacion</c>, o si llegan
    /// <c>rutas</c> y además campos de ruta del formato plano (ambiguo: no se sabe cuál vale).
    /// </summary>
    public clsResultado<clsEntradaCaptura> AEntrada()
    {
        if (!clsCodigoEnum.TryDesdeCodigo<TipoSolicitud>(Tipo, out var tipo))
        {
            return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida, "El campo 'tipo' es obligatorio y debe ser 'spot' o 'licitacion'.");
        }

        var encabezado = new clsEncabezadoSolicitudCosteo
        {
            Tipo = tipo,
            Deadline = Deadline,
            HorasCargue = HorasCargue,
            HorasDescargue = HorasDescargue,
            Notas = Notas,
        };

        if (Rutas is null)
        {
            // Formato plano anterior: una sola ruta (con la línea de negocio del encabezado).
            return new clsEntradaCaptura(NitCliente, ClienteNombre, LineaNegocio, encabezado, [new clsLineaLicitacionEntrada(null, LineaPlana())], Moneda);
        }

        if (CamposPlanos() is { Count: > 0 } planos)
        {
            // Nunca se descartan en silencio: cada campo de ruta en la raíz es un error en detalle.errores.
            return clsEnvioCosteoService.ErrorCaptura(
                [.. planos.Select(c => new clsErrorFilaLicitacion(null, null, c, clsCodigosLineaSolicitud.CampoNoAplica,
                    $"El campo '{c}' es del formato anterior (una ruta en la raíz): con 'rutas' va dentro de cada ruta."))],
                null, 1);
        }

        return new clsEntradaCaptura(NitCliente, ClienteNombre, LineaNegocio, encabezado, [.. Rutas.Select(r => r?.AEntrada())], Moneda);
    }

    /// <summary>Campos de ruta del formato plano que llegaron en la raíz (snake_case del contrato).</summary>
    private List<string> CamposPlanos()
    {
        var campos = new List<string>();
        void Si(bool viene, string campo)
        {
            if (viene)
            {
                campos.Add(campo);
            }
        }

        Si(Origen is not null, "origen");
        Si(Destino is not null, "destino");
        Si(OrigenMunicipioId is not null, "origen_municipio_id");
        Si(DestinoMunicipioId is not null, "destino_municipio_id");
        Si(Tipologia is not null, "tipologia");
        Si(ClaseViaje is not null, "clase_viaje");
        Si(ClaseContenedor is not null, "clase_contenedor");
        Si(Exclusivo is not null, "exclusivo");
        Si(Consolidado is not null, "consolidado");
        Si(RetornaContenedor is not null, "retorna_contenedor");
        Si(CiudadDevolucionContenedor is not null, "ciudad_devolucion_contenedor");
        Si(CiudadDevolucionMunicipioId is not null, "ciudad_devolucion_municipio_id");
        Si(LevanteContenedorDiferente is not null, "levante_contenedor_diferente");
        Si(CiudadLevanteContenedor is not null, "ciudad_levante_contenedor");
        Si(CiudadLevanteMunicipioId is not null, "ciudad_levante_municipio_id");
        Si(ValorAprovisionamiento is not null, "valor_aprovisionamiento");
        Si(PesoKg is not null, "peso_kg");
        Si(ValorMercancia is not null, "valor_mercancia");
        Si(Posiciones is not null, "posiciones");
        return campos;
    }

    private clsLineaSolicitudCosteo LineaPlana() => new()
    {
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
        CiudadDevolucionMunicipioId = CiudadDevolucionMunicipioId,
        LevanteContenedorDiferente = LevanteContenedorDiferente,
        CiudadLevanteContenedor = CiudadLevanteContenedor,
        CiudadLevanteMunicipioId = CiudadLevanteMunicipioId,
        ValorAprovisionamiento = ValorAprovisionamiento,
        PesoKg = PesoKg,
        ValorMercancia = ValorMercancia,
        Posiciones = clsPosicionCargaWebRequest.APosiciones(Posiciones),
    };
}

/// <summary>
/// <c>RutaCapturaWeb</c>: una ruta del formulario. Origen y destino con su id de municipio de <c>GET /web/municipios</c>
/// (obligatorio; sin él, Cotifly lo resuelve por el nombre y si es ambiguo responde el error con los candidatos) y una copia del
/// nombre; igual las ciudades de devolución y de levante del contenedor. Con el vehículo elegido (respaldo del cubicador).
/// </summary>
public class clsRutaCapturaWebRequest
{
    /// <summary>Línea de negocio de la ruta; sin ella, la de la solicitud.</summary>
    public string? LineaNegocio { get; init; }
    public int? OrigenMunicipioId { get; init; }
    public string? Origen { get; init; }
    public int? DestinoMunicipioId { get; init; }
    public string? Destino { get; init; }
    public string? Tipologia { get; init; }
    public string? ClaseViaje { get; init; }
    public string? ClaseContenedor { get; init; }
    public bool? Exclusivo { get; init; }
    public bool? Consolidado { get; init; }
    public bool? RetornaContenedor { get; init; }
    public int? CiudadDevolucionMunicipioId { get; init; }
    public string? CiudadDevolucionContenedor { get; init; }
    public bool? LevanteContenedorDiferente { get; init; }
    public int? CiudadLevanteMunicipioId { get; init; }
    public string? CiudadLevanteContenedor { get; init; }
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }

    /// <summary>Valor de UN levante del contenedor en la ruta (se copia a cada camión, decisión 4).</summary>
    public decimal? ValorLevanteContenedor { get; init; }
    public decimal? ValorMercancia { get; init; }
    public decimal? PesoKg { get; init; }

    /// <summary>
    /// Peso total de la ruta CON la tara de los contenedores (kg), opcional y solo con <c>clase_viaje = Contenedor</c>;
    /// nunca menor que <see cref="PesoKg"/>. Valor inicial sugerido: el peso de carga. Va a Nexo repartido por camión; no al cubicador.
    /// </summary>
    public decimal? PesoConTaraKg { get; init; }
    public IReadOnlyList<clsPosicionCargaWebRequest?>? Mercancias { get; init; }
    public IReadOnlyList<clsConexoCapturaWebRequest?>? ServiciosConexos { get; init; }

    /// <summary>El vehículo elegido de la ruta (código de Nexo, ADR-022). Obligatorio sin mercancías cubicables, salvo en Contenedor (<c>VEHICULO_REQUERIDO</c>); es el respaldo del cubicador: si el cubicador asigna, manda el cubicador. La carrocería es opcional.</summary>
    public string? TipoVehiculo { get; init; }

    /// <summary>El vehículo elegido de la ruta (código de Nexo, ADR-022). Obligatorio sin mercancías cubicables, salvo en Contenedor (<c>VEHICULO_REQUERIDO</c>); es el respaldo del cubicador: si el cubicador asigna, manda el cubicador. La carrocería es opcional.</summary>
    public string? Carroceria { get; init; }

    public clsLineaLicitacionEntrada AEntrada() => new(
        null,
        new clsLineaSolicitudCosteo
        {
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
            CiudadDevolucionMunicipioId = CiudadDevolucionMunicipioId,
            LevanteContenedorDiferente = LevanteContenedorDiferente,
            CiudadLevanteContenedor = CiudadLevanteContenedor,
            CiudadLevanteMunicipioId = CiudadLevanteMunicipioId,
            ValorAprovisionamiento = ValorLevanteContenedor,
            PesoKg = PesoKg,
            ValorMercancia = ValorMercancia,
            PesoConTaraKg = PesoConTaraKg,
            HorasCargue = HorasCargue,
            HorasDescargue = HorasDescargue,
            Posiciones = clsPosicionCargaWebRequest.APosiciones(Mercancias),
        },
        LineaNegocio,
        clsConexoCapturaWebRequest.AEntradas(ServiciosConexos));
}

/// <summary><c>ConexoCapturaWeb</c>: un servicio del catálogo de Nexo para la ruta; el precio lo pone Cotifly (costo de Nexo).</summary>
public sealed class clsConexoCapturaWebRequest
{
    public string? IdServicioConexo { get; init; }
    public decimal? Cantidad { get; init; }

    public static IReadOnlyList<clsConexoCapturaEntrada?>? AEntradas(IReadOnlyList<clsConexoCapturaWebRequest?>? conexos) =>
        conexos is null ? null : [.. conexos.Select(c => c is null ? null : new clsConexoCapturaEntrada(c.IdServicioConexo, c.Cantidad))];
}

/// <summary>
/// <c>MercanciaWeb</c> / <c>PosicionCargaWeb</c>: una mercancía (bulto) en centímetros y kilogramos; <c>cantidad</c> por defecto 1.
/// <c>codigo_mercancia</c> (catálogo del cubicador, <c>GET /web/tipos-mercancia</c>) es obligatorio en una captura nueva.
/// <c>id_tipo_mercancia</c> (uuid) es <b>obsoleto</b>: se acepta y se traduce al código con el catálogo.
/// <c>nombre_mercancia</c> es la copia de su nombre (<c>descripcion</c> es el nombre anterior del mismo campo).
/// </summary>
public sealed class clsPosicionCargaWebRequest
{
    /// <summary>Código del catálogo del cubicador (por ejemplo <c>BEBIDAS</c>).</summary>
    public string? CodigoMercancia { get; init; }

    /// <summary>Obsoleto: el uuid del catálogo; se traduce al código. Usar <see cref="CodigoMercancia"/>.</summary>
    public Guid? IdTipoMercancia { get; init; }
    public string? NombreMercancia { get; init; }
    public string? Descripcion { get; init; }
    public int Cantidad { get; init; } = 1;

    /// <summary>
    /// Medidas en cm, opcionales. Sin ninguna (ausentes o <c>null</c>) y sin <c>codigo_mercancia</c> la mercancía es "no
    /// cubicable": se guarda, no viaja a Nexo como posición y la ruta usa el vehículo elegido. Si viene una, vienen las tres.
    /// </summary>
    public decimal? LargoCm { get; init; }
    public decimal? AnchoCm { get; init; }
    public decimal? AltoCm { get; init; }
    public decimal? PesoKg { get; init; }

    public clsPosicionCarga APosicion() => new(
        NombreMercancia ?? Descripcion, Cantidad, LargoCm ?? 0m, AnchoCm ?? 0m, AltoCm ?? 0m, PesoKg, IdTipoMercancia,
        string.IsNullOrWhiteSpace(CodigoMercancia) ? null : CodigoMercancia.Trim());

    /// <summary>Las mercancías del contrato como posiciones del dominio (un elemento <c>null</c> se conserva: lo reporta la validación).</summary>
    public static IReadOnlyList<clsPosicionCarga> APosiciones(IReadOnlyList<clsPosicionCargaWebRequest?>? posiciones) =>
        [.. (posiciones ?? []).Select(p => p is null ? null! : p.APosicion())];
}

/// <summary>
/// <c>SeleccionServiciosConexos</c> (por ruta): <c>conexos</c> (los que quedan en la ruta, con su cantidad;
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
/// <c>AjusteMargenWeb</c> (por ruta): <c>margen</c> (fracción de 0 a 0,99), <c>justificacion</c>
/// (obligatoria por debajo del margen sugerido de la ruta) y la ruta (<c>linea_orden</c> o <c>linea_solicitud_id</c>; opcional si la
/// cotización tiene una sola). Los límites los valida el dominio para responder <c>AJUSTE_MARGEN_INVALIDO</c>,
/// <c>JUSTIFICACION_REQUERIDA</c>, <c>RUTA_REQUERIDA</c> o <c>RUTA_INEXISTENTE</c>.
/// </summary>
public sealed record clsAjusteMargenWebRequest(decimal? Margen, string? Justificacion, int? LineaOrden = null, Guid? LineaSolicitudId = null);

/// <summary>
/// <c>SeguroRutaWeb</c>: <c>incluye_seguro</c> (obligatorio: <c>true</c> incluye el seguro de la mercancía en la ruta,
/// <c>false</c> lo quita) y la ruta (<c>linea_orden</c> o <c>linea_solicitud_id</c>; sin ninguno, la única ruta), como el ajuste de margen.
/// </summary>
public sealed record clsSeguroRutaWebRequest(bool? IncluyeSeguro, int? LineaOrden = null, Guid? LineaSolicitudId = null);

/// <summary>
/// <c>FinalizarWeb</c>, cuerpo OPCIONAL de <c>POST .../finalizar</c>: <c>justificacion</c> (de 10 a
/// 500 caracteres, sin los espacios de los extremos) para finalizar aunque falte una medida obligatoria de la política de seguros. Salta solo
/// ese bloqueo y queda auditada; ausente, vacía o solo espacios es como no mandarla.
/// </summary>
public sealed record clsFinalizarWebRequest(string? Justificacion);

/// <summary><c>ResultadoWeb</c>: <c>resultado</c> (<c>ganada</c> | <c>perdida</c>), <c>motivo</c> (se guarda en <c>motivo_rechazo</c>) y
/// <c>comentario</c> (solo se reenvía a Nexo).</summary>
public sealed record clsResultadoWebRequest(string? Resultado, string? Motivo, string? Comentario);

/// <summary>
/// <c>ReSolicitudCosteoWeb</c>: <c>motivo</c>, <c>datos_carga</c> (<c>DatosCargaWeb</c>; ausente o <c>null</c> = sin cambios) y
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
/// <c>DatosCargaWeb</c>: los mismos campos de línea que la captura (más las horas de cargue y descargue), solo los que
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

    /// <summary>El id del municipio de devolución, como en la captura.</summary>
    public int? CiudadDevolucionMunicipioId { get; init; }
    public bool? LevanteContenedorDiferente { get; init; }
    public string? CiudadLevanteContenedor { get; init; }

    /// <summary>El id del municipio de levante.</summary>
    public int? CiudadLevanteMunicipioId { get; init; }
    public decimal? ValorAprovisionamiento { get; init; }
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }
    public decimal? PesoKg { get; init; }
    public decimal? ValorMercancia { get; init; }

    /// <summary>Peso total con tara de la ruta de contenedor (<c>null</c> lo quita).</summary>
    public decimal? PesoConTaraKg { get; init; }
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
            CiudadDevolucionMunicipioId = leido.CiudadDevolucionMunicipioId,
            LevanteContenedorDiferente = leido.LevanteContenedorDiferente,
            CiudadLevanteContenedor = leido.CiudadLevanteContenedor,
            CiudadLevanteMunicipioId = leido.CiudadLevanteMunicipioId,
            ValorAprovisionamiento = leido.ValorAprovisionamiento,
            HorasCargue = leido.HorasCargue,
            HorasDescargue = leido.HorasDescargue,
            PesoKg = leido.PesoKg,
            ValorMercancia = leido.ValorMercancia,
            PesoConTaraKg = leido.PesoConTaraKg,
            Posiciones = leido.Posiciones is null
                ? null
                // Con codigo_mercancia y nombre_mercancia, para que la -vN no caiga en el respaldo.
                : [.. leido.Posiciones.Select(p => p is null ? null! : p.APosicion())],
        };
    }

    private static clsErrorDominio Invalido(string mensaje) => new(clsCodigosError.SolicitudCosteoInvalida, mensaje);
}
