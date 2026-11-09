using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cotifly.Api.Comun;
using Cotifly.Dominio.Comun;
using Cotifly.Pruebas.Contrato;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cotifly.Api.Tests.Contrato;

/// <summary>
/// Endurecimiento A (#199): prueba de contrato por reflexión. Para cada DTO de <see cref="clsContratoRutasTests.MapaDtos"/> compara,
/// propiedad por propiedad (las que el esquema declara; que los nombres sean exactamente los del esquema lo prueba
/// <see cref="clsContratoRutasTests"/>):
/// <list type="bullet">
/// <item><b>tipo JSON</b>: el que produce System.Text.Json con <see cref="clsJsonWeb.Opciones"/> (texto, entero, número, booleano, arreglo u
/// objeto) contra el <c>type</c> del esquema (resolviendo <c>$ref</c>, <c>oneOf</c> con <c>Nulo</c> y <c>allOf</c>);</item>
/// <item><b>format</b>: <see cref="Guid"/> ⇔ <c>uuid</c>, <see cref="DateTimeOffset"/>/<see cref="DateTime"/> ⇔ <c>date-time</c>,
/// <see cref="DateOnly"/> ⇔ <c>date</c>, en los dos sentidos;</item>
/// <item><b>nulabilidad</b>: en una respuesta, una propiedad anulable en C# que se escribe como <c>null</c> (la política es escribir los
/// nulos) debe admitir <c>null</c> en el esquema; una con <c>[JsonIgnore(WhenWritingNull)]</c> no puede ser <c>required</c>; en una
/// entrada, un tipo de valor no anulable no puede admitir <c>null</c> (System.Text.Json lo rechaza con 400);</item>
/// <item><b>enums</b>: los valores del <c>enum</c> del esquema son los códigos del enum C# (<see cref="clsCodigoEnum"/>) en
/// <see cref="EnumsDelDominio"/>; toda propiedad con <c>enum</c> está ahí o en <see cref="EnumsSinTipoCSharp"/> con su motivo.</item>
/// </list>
/// Además, los códigos de <c>ErrorNegocio.codigo</c>: cada constante de <see cref="clsCodigosError"/> que el código usa (un <c>ldstr</c>
/// con su valor en el IL de la API, la Aplicación, la Infraestructura o el Dominio) está en el <c>enum</c>, y cada valor del <c>enum</c> lo
/// emite algún código (el contrato no anuncia códigos muertos). Que cada <c>ProducesResponseType</c> de un controller esté en el contrato
/// lo prueba <see cref="clsContratoDocumentoTests.CadaProducesResponseTypeDeUnaAccionEstaEnElContratoConElEsquemaDeSuDto"/>.
/// </summary>
public sealed class clsContratoReflexionTests(clsCotiflyApiFactory factory) : IClassFixture<clsCotiflyApiFactory>
{
    /// <summary>"Esquema.propiedad" → enum del dominio cuyos códigos son el <c>enum</c> del esquema (menos los excluidos, más los extra).</summary>
    public static readonly IReadOnlyDictionary<string, (Type Enum, string[] Excluidos, string[] Extra)> EnumsDelDominio =
        new Dictionary<string, (Type, string[], string[])>(StringComparer.Ordinal)
        {
            // La cabecera nunca queda "rechazada" (HU-028.1, #183): solo las rutas y los camiones.
            ["CotizacionResumen.estado_costeo"] = (typeof(Cotifly.Dominio.Cotizaciones.EstadoCosteo), ["rechazada"], []),
            ["CosteoCotizacion.estado_costeo"] = (typeof(Cotifly.Dominio.Cotizaciones.EstadoCosteo), ["rechazada"], []),
            ["Licitacion.estado_costeo"] = (typeof(Cotifly.Dominio.Cotizaciones.EstadoCosteo), ["rechazada"], []),
            ["RutaCosteo.estado_costeo"] = (typeof(Cotifly.Dominio.Cotizaciones.EstadoCosteo), [], []),
            ["CamionCosteo.estado_costeo"] = (typeof(Cotifly.Dominio.Cotizaciones.EstadoCosteo), [], []),
            ["LineaLicitacion.estado_costeo"] = (typeof(Cotifly.Dominio.Cotizaciones.EstadoCosteo), [], []),
            ["CamionLicitacion.estado_costeo"] = (typeof(Cotifly.Dominio.Cotizaciones.EstadoCosteo), [], []),
            ["CotizacionResumen.estado_envio"] = (typeof(Cotifly.Dominio.Cotizaciones.EstadoEnvio), [], []),
            ["CotizacionResumen.origen_cliente"] = (typeof(Cotifly.Dominio.Clientes.OrigenCliente), [], []),
            ["ClienteEncontrado.origen_cliente"] = (typeof(Cotifly.Dominio.Clientes.OrigenCliente), [], []),
            ["DatosNuevaVersion.origen_cliente"] = (typeof(Cotifly.Dominio.Clientes.OrigenCliente), [], []),
            ["BloqueadosSolicitud.origen_cliente"] = (typeof(Cotifly.Dominio.Clientes.OrigenCliente), [], []),
            ["SalidaMargen.origen"] = (typeof(Cotifly.Dominio.Margen.OrigenMargen), [], []),
            ["CotizacionResumen.origen_margen"] = (typeof(Cotifly.Dominio.Margen.OrigenMargen), [], []),
            ["RutaCosteo.origen_margen"] = (typeof(Cotifly.Dominio.Margen.OrigenMargen), [], []),
            ["CosteoCotizacion.origen_margen"] = (typeof(Cotifly.Dominio.Margen.OrigenMargen), [], []),
            ["RutaEditable.origen_margen"] = (typeof(Cotifly.Dominio.Margen.OrigenMargen), [], []),
            ["DatosNuevaVersion.origen_margen"] = (typeof(Cotifly.Dominio.Margen.OrigenMargen), [], []),
            ["RutaNuevaVersion.origen_margen"] = (typeof(Cotifly.Dominio.Margen.OrigenMargen), [], []),
            ["RespuestaEnvio.estado"] = (typeof(Cotifly.Dominio.Correo.EstadoCorreo), [], []),
            ["EnvioCorreo.estado"] = (typeof(Cotifly.Dominio.Correo.EstadoCorreo), [], []),
            ["LineaLicitacion.estado_cubicacion"] = (typeof(Cotifly.Dominio.Cotizaciones.EstadoCubicacion), [], []),
            // solo_cotifly no es un estado guardado: es la lectura de una cotización cuyo resultado nunca va a Nexo (HU-033).
            ["CotizacionResumen.sincronizacion_resultado_nexo"] = (typeof(Cotifly.Dominio.Cotizaciones.SincronizacionResultadoNexo), [], ["solo_cotifly"]),
            ["CosteoCotizacion.sincronizacion_resultado_nexo"] = (typeof(Cotifly.Dominio.Cotizaciones.SincronizacionResultadoNexo), [], ["solo_cotifly"]),
            ["DetalleCotizacion.tipo"] = (typeof(Cotifly.Dominio.Cotizaciones.TipoSolicitud), [], []),
            ["BloqueadosSolicitud.tipo"] = (typeof(Cotifly.Dominio.Cotizaciones.TipoSolicitud), [], []),
            ["SolicitudCosteoWeb.tipo"] = (typeof(Cotifly.Dominio.Cotizaciones.TipoSolicitud), [], []),
            ["EdicionSolicitudResultado.modo"] = (typeof(Cotifly.Dominio.Cotizaciones.ModoEdicionSolicitud), [], []),
            ["EstadoEdicion.modo"] = (typeof(Cotifly.Dominio.Cotizaciones.ModoEdicionSolicitud), [], []),
            ["EdicionSolicitudWeb.modo_esperado"] = (typeof(Cotifly.Dominio.Cotizaciones.ModoEdicionSolicitud), [], []),
            ["AliasResueltos.tipo"] = (typeof(Cotifly.Aplicacion.CatalogosFlete.DTOs.TipoAliasNexo), [], []),
            ["AliasResuelto.fuente"] = (typeof(Cotifly.Aplicacion.CatalogosFlete.Services.FuenteCodigoNexo), [], []),
        };

    /// <summary>
    /// Propiedades con <c>enum</c> en el esquema que no salen de un enum C# (constantes de texto del código o valores fijos). Su valor lo
    /// cubren las pruebas de endpoints que validan cada respuesta contra el esquema.
    /// </summary>
    public static readonly IReadOnlySet<string> EnumsSinTipoCSharp = new HashSet<string>(StringComparer.Ordinal)
    {
        "ErrorFilaLicitacion.codigo",
        "MensajeWeb.moneda", "SalidaPrecio.moneda", "ResumenCorreo.moneda", "EdicionSolicitudWeb.moneda", "SolicitudEditable.moneda",
        "SolicitudCosteoWeb.moneda", "LicitacionWeb.moneda", "Licitacion.moneda",
        "SalidaPrecio.metodo", "LineaNegocio.metodo_calculo", "ActualizarLineaNegocio.metodo_calculo", "DatosNuevaVersion.metodo_calculo",
        "RutaNuevaVersion.metodo_calculo", "SalidaPrecio.trm_fuente",
        "AccionPropuesta.tipo", "AccionPropuesta.metodo", "RespuestaChatError.error", "RespuestaChatError.codigo",
        "CotizacionResumen.avisos", "RespuestaEnvio.enviado",
        "CubicacionRuta.resultado", "CubicacionRuta.motivo_sin_seleccion", "RutaCosteo.resultado_cubicacion", "RutaCosteo.motivo_sin_seleccion",
        "LineaLicitacion.resultado_cubicacion", "LineaLicitacion.motivo_sin_seleccion", "ResultadoCubicacion.resultado",
        "ResultadoCubicacion.motivo_sin_seleccion", "SugerenciaVehiculoLegada.banda",
        "CotizacionesRegional.fuente_regional", "MetricasRegional.fuente_regional",
        "ResultadoWeb.resultado", "CosteoCotizacion.resultado", "ServicioConexoCosteo.estado_nexo", "TextosCotizacion.fuente",
        "EdicionSolicitudResultado.solicitud_nexo_reemplazada", "CodigoMercanciaFila.origen",
        "CubicacionEnProceso.estado", "EstadoCubicacion.estado",
        // HU-042.2 (#243): el estado de la corrida es el texto del CHECK de SapMargenSyncLog (constantes de clsCorridaSincronizacionMargenSap).
        "CorridaSincronizacionMargenSap.estado", "ResultadoSincronizacionMargenSap.estado",
        // HU-044.4 (#255): el campo son las constantes de clsCamposNormalizacion y el estado es el texto de NEXO, que Cotifly reenvía.
        "ValorNormalizado.campo", "ValorNormalizado.estado",
    };

    /// <summary>
    /// Respuestas cuyo <c>format</c> del contrato es más estricto que el tipo C# (texto) porque lo garantiza el origen del dato, con el
    /// motivo. En las entradas no se exige: el contrato dice qué debe mandar el cliente y el servidor valida el texto (400).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> FormatoGarantizadoPorElOrigen = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ServicioConexoCatalogo.id"] = "Es el id (Guid) del servicio en el catálogo de Nexo, que Cotifly reenvía como texto sin interpretarlo.",
    };

    private static readonly NullabilityInfoContext _nulabilidad = new();

    [Fact]
    public async Task CadaPropiedadDeCadaDtoTieneElTipoElFormatoYLaNulabilidadDeSuEsquema()
    {
        var (respuestas, entradas) = TiposPorDireccion();
        var errores = new List<string>();
        foreach (var (dto, nombreEsquema) in clsContratoRutasTests.MapaDtos)
        {
            var esquema = await clsValidadorEsquemaOpenApi.EsquemaAsync(nombreEsquema);
            var propiedades = esquema.Properties ?? new Dictionary<string, IOpenApiSchema>();
            var requeridas = esquema.Required ?? new HashSet<string>();
            var esRespuesta = respuestas.Contains(dto);
            var esEntrada = entradas.Contains(dto);
            foreach (var propiedad in clsJsonWeb.Opciones.GetTypeInfo(dto).Properties)
            {
                if (!propiedades.TryGetValue(propiedad.Name, out var deEsquema) || EsIgnoradaSiempre(propiedad))
                {
                    continue; // nombres de más o de menos: clsContratoRutasTests.
                }

                var donde = $"{nombreEsquema}.{propiedad.Name} ({dto.Name}.{(propiedad.AttributeProvider as MemberInfo)?.Name})";
                var tipo = Nullable.GetUnderlyingType(propiedad.PropertyType) ?? propiedad.PropertyType;
                var (tiposEsquema, admiteNulo, formato) = Efectivo(deEsquema);

                // Tipo JSON.
                var tipoCSharp = TipoJson(tipo);
                if (tipoCSharp is { } t && tiposEsquema != 0 && (tiposEsquema & t) == 0
                    && !(t == JsonSchemaType.Integer && tiposEsquema.HasFlag(JsonSchemaType.Number)))
                {
                    errores.Add($"{donde}: tipo {t} en C# y {tiposEsquema} en el contrato");
                }

                // Formato.
                var formatoCSharp = FormatoDe(tipo);
                if (formatoCSharp is not null && !string.Equals(formato, formatoCSharp, StringComparison.Ordinal))
                {
                    errores.Add($"{donde}: {tipo.Name} en C# exige format: {formatoCSharp} (el contrato dice '{formato}')");
                }
                else if (esRespuesta && formato is "uuid" or "date-time" or "date" && formatoCSharp != formato && tipoCSharp == JsonSchemaType.String
                         && !FormatoGarantizadoPorElOrigen.ContainsKey($"{nombreEsquema}.{propiedad.Name}"))
                {
                    errores.Add($"{donde}: el contrato dice format: {formato} y C# es {tipo.Name} (no garantiza el formato)");
                }

                // Nulabilidad.
                var anulable = EsAnulable(propiedad);
                var omiteNulo = OmiteNulo(propiedad);
                if (esRespuesta && anulable && !omiteNulo && !admiteNulo)
                {
                    errores.Add($"{donde}: anulable en C# y se escribe null, pero el contrato no admite null");
                }

                if (esRespuesta && omiteNulo && requeridas.Contains(propiedad.Name))
                {
                    errores.Add($"{donde}: [JsonIgnore(WhenWritingNull)] la omite, pero el contrato la declara required");
                }

                if (esEntrada && !esRespuesta && tipo.IsValueType && Nullable.GetUnderlyingType(propiedad.PropertyType) is null && admiteNulo)
                {
                    errores.Add($"{donde}: el contrato admite null en la entrada, pero {tipo.Name} no anulable lo rechaza (400)");
                }
            }
        }

        errores.Should().BeEmpty(string.Join(Environment.NewLine, errores));
    }

    [Fact]
    public async Task LosEnumsDelContratoSonLosCodigosDeLosEnumsDelDominio()
    {
        var errores = new List<string>();
        var conEnum = new List<string>();
        foreach (var (_, nombreEsquema) in clsContratoRutasTests.MapaDtos.DistinctBy(m => m.Esquema))
        {
            var esquema = await clsValidadorEsquemaOpenApi.EsquemaAsync(nombreEsquema);
            foreach (var (nombre, propiedad) in esquema.Properties ?? new Dictionary<string, IOpenApiSchema>())
            {
                var valores = ValoresEnum(propiedad);
                if (valores is null)
                {
                    continue;
                }

                var clave = $"{nombreEsquema}.{nombre}";
                conEnum.Add(clave);
                if (!EnumsDelDominio.TryGetValue(clave, out var mapeo))
                {
                    continue;
                }

                var codigos = ((IEnumerable<string>)typeof(clsCodigoEnum).GetMethod(nameof(clsCodigoEnum.Codigos))!
                        .MakeGenericMethod(mapeo.Enum).Invoke(null, null)!)
                    .Except(mapeo.Excluidos).Concat(mapeo.Extra).ToHashSet(StringComparer.Ordinal);
                if (!valores.SetEquals(codigos))
                {
                    errores.Add($"{clave}: el contrato dice [{string.Join(", ", valores.Order())}] y {mapeo.Enum.Name} [{string.Join(", ", codigos.Order())}]");
                }
            }
        }

        errores.Should().BeEmpty(string.Join(Environment.NewLine, errores));
        var sinMapear = conEnum.Where(c => !EnumsDelDominio.ContainsKey(c) && !EnumsSinTipoCSharp.Contains(c)).ToList();
        sinMapear.Should().BeEmpty($"{string.Join(", ", sinMapear)}: cada propiedad con enum se compara con su enum C# (EnumsDelDominio) o explica por qué no (EnumsSinTipoCSharp)");
        var sobrantes = EnumsDelDominio.Keys.Concat(EnumsSinTipoCSharp).Where(c => !conEnum.Contains(c)).ToList();
        sobrantes.Should().BeEmpty($"{string.Join(", ", sobrantes)}: una entrada que ya no es un enum del contrato sobra");
    }

    [Fact]
    public async Task ElEnumDeErrorNegocioEsExactamenteElDeLosCodigosQueElCodigoEmite()
    {
        var esquema = await clsValidadorEsquemaOpenApi.EsquemaAsync("ErrorNegocio");
        var enumContrato = ValoresEnum(esquema.Properties!["error"].Properties!["codigo"])!;
        var literales = LiteralesDelIl();
        var constantes = typeof(clsCodigosError).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        var usadasFueraDelContrato = constantes.Where(literales.Contains).Where(c => !enumContrato.Contains(c)).Order().ToList();
        var muertasEnElContrato = enumContrato.Where(c => !literales.Contains(c)).Order().ToList();

        usadasFueraDelContrato.Should().BeEmpty($"{string.Join(", ", usadasFueraDelContrato)}: cada código de clsCodigosError que el código emite está en el enum de ErrorNegocio.codigo");
        muertasEnElContrato.Should().BeEmpty($"{string.Join(", ", muertasEnElContrato)}: el enum de ErrorNegocio.codigo no anuncia códigos que ningún código emite");

        // Integración de los endurecimientos A y B (#199): FOLIO_INVALIDO vive en clsFolio.CodigoInvalido (Dominio), no en clsCodigosError;
        // está en el enum porque el código lo emite (re-solicitar -vN y nueva_version), aunque no sea una constante de clsCodigosError.
        constantes.Should().NotContain(Cotifly.Dominio.Cotizaciones.clsFolio.CodigoInvalido, "FOLIO_INVALIDO no es de clsCodigosError");
        enumContrato.Should().Contain(Cotifly.Dominio.Cotizaciones.clsFolio.CodigoInvalido);
        literales.Should().Contain(Cotifly.Dominio.Cotizaciones.clsFolio.CodigoInvalido);
        // COTIZACION_NEXO_NO_ENCONTRADA (endurecimiento B) es una constante de clsCodigosError, está en el enum y la emite el adaptador
        // de Nexo (no basta con que la mencione la tabla de traducción HTTP de clsRespuestasWeb).
        enumContrato.Should().Contain(clsCodigosError.CotizacionNexoNoEncontrada);
        LiteralesDe(typeof(Cotifly.Infraestructura.clsDependenciasInfraestructura).Assembly)
            .Should().Contain(clsCodigosError.CotizacionNexoNoEncontrada, "clsCosteoNexo la emite ante el 404 de Nexo");
    }

    /// <summary>Tipos (con sus anidados mapeados) que salen en respuestas y que entran por el cuerpo de las acciones de /web y /pdfs.</summary>
    private (HashSet<Type> Respuestas, HashSet<Type> Entradas) TiposPorDireccion()
    {
        var mapeados = clsContratoRutasTests.MapaDtos.Select(m => m.Dto).ToHashSet();
        var respuestas = new HashSet<Type>();
        var entradas = new HashSet<Type>();
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                     .Where(e => e.RoutePattern.RawText is { } r && (r.StartsWith("web/", StringComparison.Ordinal) || r.StartsWith("pdfs/", StringComparison.Ordinal))))
        {
            foreach (var producido in endpoint.Metadata.OfType<ProducesResponseTypeAttribute>().Select(p => p.Type))
            {
                AgregarConAnidados(ElementoDe(producido), mapeados, respuestas);
            }

            if (endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } accion)
            {
                foreach (var parametro in accion.Parameters.Where(p => p.BindingInfo?.BindingSource == BindingSource.Body))
                {
                    AgregarConAnidados(ElementoDe(parametro.ParameterType), mapeados, entradas);
                }
            }
        }

        respuestas.Should().NotBeEmpty();
        entradas.Should().NotBeEmpty();
        return (respuestas, entradas);
    }

    private static void AgregarConAnidados(Type tipo, HashSet<Type> mapeados, HashSet<Type> destino)
    {
        tipo = Nullable.GetUnderlyingType(tipo) ?? tipo;
        if (!mapeados.Contains(tipo) || !destino.Add(tipo))
        {
            return;
        }

        foreach (var propiedad in clsJsonWeb.Opciones.GetTypeInfo(tipo).Properties)
        {
            AgregarConAnidados(ElementoDe(propiedad.PropertyType), mapeados, destino);
        }
    }

    private static Type ElementoDe(Type tipo)
    {
        tipo = Nullable.GetUnderlyingType(tipo) ?? tipo;
        if (tipo != typeof(string) && typeof(IEnumerable).IsAssignableFrom(tipo))
        {
            if (tipo.IsArray)
            {
                return tipo.GetElementType()!;
            }

            var argumentos = tipo.GetGenericArguments();
            return argumentos.Length > 0 ? argumentos[^1] : tipo;
        }

        return tipo;
    }

    /// <summary>Tipos JSON, si admite null y el formato del esquema de una propiedad (resolviendo <c>$ref</c>, <c>oneOf</c> y <c>allOf</c>).</summary>
    private static (JsonSchemaType Tipos, bool AdmiteNulo, string? Formato) Efectivo(IOpenApiSchema esquema)
    {
        var tipos = esquema.Type ?? 0;
        var nulo = tipos.HasFlag(JsonSchemaType.Null) || (esquema.Enum?.Any(EsNulo) ?? false);
        var formato = esquema.Format;
        foreach (var alternativa in (esquema.OneOf ?? []).Concat(esquema.AnyOf ?? []).Concat(esquema.AllOf ?? []))
        {
            if (alternativa is OpenApiSchemaReference { Reference.Id: "Nulo" })
            {
                nulo = true;
                continue;
            }

            var (t, n, f) = Efectivo(alternativa);
            tipos |= t;
            nulo |= n;
            formato ??= f;
        }

        // Una referencia a un objeto sin type explícito (o solo con propiedades) es un objeto.
        if ((tipos & ~JsonSchemaType.Null) == 0 && (esquema.Properties?.Count > 0 || esquema.AdditionalProperties is not null))
        {
            tipos |= JsonSchemaType.Object;
        }

        return (tipos & ~JsonSchemaType.Null, nulo, formato);
    }

    /// <summary>Tipo JSON que escribe System.Text.Json para el tipo C# (o <c>null</c> si es libre: object, JsonElement...).</summary>
    private static JsonSchemaType? TipoJson(Type tipo)
    {
        if (tipo == typeof(object) || tipo == typeof(JsonElement) || typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(tipo))
        {
            return null;
        }

        if (tipo == typeof(string) || tipo == typeof(Guid) || tipo == typeof(DateTime) || tipo == typeof(DateTimeOffset) || tipo == typeof(DateOnly)
            || tipo == typeof(TimeSpan) || tipo == typeof(char) || tipo == typeof(Uri))
        {
            return JsonSchemaType.String;
        }

        if (tipo == typeof(bool))
        {
            return JsonSchemaType.Boolean;
        }

        if (tipo.IsEnum)
        {
            return JsonSerializer.Serialize(Activator.CreateInstance(tipo), tipo, clsJsonWeb.Opciones).StartsWith('"') ? JsonSchemaType.String : JsonSchemaType.Integer;
        }

        if (tipo == typeof(int) || tipo == typeof(long) || tipo == typeof(short) || tipo == typeof(byte) || tipo == typeof(uint) || tipo == typeof(ulong))
        {
            return JsonSchemaType.Integer;
        }

        if (tipo == typeof(decimal) || tipo == typeof(double) || tipo == typeof(float))
        {
            return JsonSchemaType.Number;
        }

        if (typeof(IDictionary).IsAssignableFrom(tipo)
            || tipo.GetInterfaces().Append(tipo).Any(i => i.IsGenericType
                && (i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IDictionary<,>))))
        {
            return JsonSchemaType.Object;
        }

        return typeof(IEnumerable).IsAssignableFrom(tipo) ? JsonSchemaType.Array : JsonSchemaType.Object;
    }

    private static string? FormatoDe(Type tipo) =>
        tipo == typeof(Guid) ? "uuid"
        : tipo == typeof(DateTimeOffset) || tipo == typeof(DateTime) ? "date-time"
        : tipo == typeof(DateOnly) ? "date"
        : null;

    private static bool EsIgnoradaSiempre(JsonPropertyInfo propiedad) =>
        propiedad.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).OfType<JsonIgnoreAttribute>()
            .Any(a => a.Condition == JsonIgnoreCondition.Always) == true
        || (propiedad.AttributeProvider?.IsDefined(typeof(clsCampoRetiradoAttribute), inherit: true) ?? false);

    private static bool OmiteNulo(JsonPropertyInfo propiedad) =>
        propiedad.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).OfType<JsonIgnoreAttribute>()
            .Any(a => a.Condition is JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault) == true;

    private static bool EsAnulable(JsonPropertyInfo propiedad)
    {
        if (propiedad.PropertyType.IsValueType)
        {
            return Nullable.GetUnderlyingType(propiedad.PropertyType) is not null;
        }

        return propiedad.AttributeProvider is PropertyInfo info && _nulabilidad.Create(info).ReadState == NullabilityState.Nullable;
    }

    /// <summary>Valores de texto del <c>enum</c> de una propiedad (o de sus <c>items</c>), sin <c>null</c>; <c>null</c> si no tiene.</summary>
    private static HashSet<string>? ValoresEnum(IOpenApiSchema esquema)
    {
        var lista = esquema.Enum ?? esquema.Items?.Enum;
        if (lista is null || lista.Count == 0)
        {
            foreach (var alternativa in esquema.OneOf ?? [])
            {
                if (ValoresEnum(alternativa) is { } deAlternativa)
                {
                    return deAlternativa;
                }
            }

            return null;
        }

        return lista.Where(v => !EsNulo(v)).Select(v => v!.ToString()).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary><c>null</c> en un <c>enum</c> (Microsoft.OpenApi lo representa con un valor centinela).</summary>
    private static bool EsNulo(System.Text.Json.Nodes.JsonNode? valor) =>
        valor is null || valor.GetValueKind() == JsonValueKind.Null
        || valor.ToString().StartsWith("openapi-json-null-sentinel-value", StringComparison.Ordinal);

    /// <summary>Todos los <c>ldstr</c> del IL de los ensamblados de Cotifly (Api, Aplicación, Infraestructura y Dominio).</summary>
    public static IReadOnlySet<string> LiteralesDelIl() => _literales.Value;

    private static readonly Lazy<HashSet<string>> _literales = new(LeerLiteralesDelIl);

    private static HashSet<string> LeerLiteralesDelIl() =>
        LiteralesDe(typeof(Program).Assembly, typeof(Cotifly.Aplicacion.Correo.Services.clsOpcionesCorreo).Assembly, typeof(Cotifly.Infraestructura.clsDependenciasInfraestructura).Assembly, typeof(clsCodigosError).Assembly);

    /// <summary>Literales de texto (<c>ldstr</c>) del IL de los ensamblados dados.</summary>
    private static HashSet<string> LiteralesDe(params Assembly[] ensamblados)
    {
        var operandos = new Dictionary<short, OperandType>();
        foreach (var campo in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var codigo = (OpCode)campo.GetValue(null)!;
            operandos[codigo.Value] = codigo.OperandType;
        }

        var literales = new HashSet<string>(StringComparer.Ordinal);
        const BindingFlags Todos = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var ensamblado in ensamblados)
        {
            foreach (var tipo in ensamblado.GetTypes())
            {
                foreach (var metodo in tipo.GetMethods(Todos).Cast<MethodBase>().Concat(tipo.GetConstructors(Todos)))
                {
                    var il = metodo.GetMethodBody()?.GetILAsByteArray();
                    if (il is null)
                    {
                        continue;
                    }

                    for (var i = 0; i < il.Length;)
                    {
                        short valor = il[i] == 0xFE && i + 1 < il.Length ? (short)(0xFE00 | il[i + 1]) : il[i];
                        i += il[i] == 0xFE ? 2 : 1;
                        var operando = operandos.TryGetValue(valor, out var o) ? o : OperandType.InlineNone;
                        if (valor == OpCodes.Ldstr.Value)
                        {
                            literales.Add(metodo.Module.ResolveString(BitConverter.ToInt32(il, i)));
                        }

                        i += operando switch
                        {
                            OperandType.InlineNone => 0,
                            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                            OperandType.InlineVar => 2,
                            OperandType.InlineI8 or OperandType.InlineR => 8,
                            OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                            _ => 4,
                        };
                    }
                }
            }
        }

        return literales;
    }
}
