using System.ComponentModel;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.SemanticKernel;

namespace Cotifly.Aplicacion.Agente.Herramientas;

/// <summary>
/// Plugin <c>asistente</c> de Semantic Kernel (HU-013.6, #102, ADR-018 §2b): las herramientas del asistente por folio. Solo LEEN o
/// PROPONEN; ninguna escribe en la base ni llama a Nexo con efecto. Reglas:
/// <list type="bullet">
/// <item>La cotización sale de <see cref="clsContextoAsistenteFolio"/> (la del folio de la conversación, ya validada como del
/// comercial), nunca de un argumento del LLM.</item>
/// <item><b>Ningún monto de dinero va al LLM</b> (ni precio, ni costo, ni totales, ni valores de conexos, ni TRM, ni valores de la
/// mercancía): solo porcentajes de margen, estados, rutas, nombres y cantidades. Con el margen y un precio el LLM podría deducir el
/// costo de Nexo.</item>
/// <item>Las propuestas las arma <see cref="clsPropuestasAsistente"/> (cuerpo exacto, endpoint, <c>row_version</c> y resumen con los
/// precios calculados por el servidor); el resumen queda en el contexto y nunca vuelve al modelo.</item>
/// </list>
/// </summary>
public sealed class clsHerramientasAsistenteFolio(
    clsContextoAsistenteFolio contexto,
    IClsCotizacionRepository cotizaciones,
    IClsPrecioService precios,
    IClsServiciosConexosCosteoService conexos,
    IClsReSolicitudCosteoService reSolicitud)
{
    /// <summary>Servicios del catálogo que ve el modelo por búsqueda.</summary>
    public const int MaximoServicios = 10;

    /// <summary>Instrucción que acompaña a toda propuesta (lo que el modelo debe decirle al comercial).</summary>
    public const string MensajePropuesta =
        "Propuesta lista. El comercial verá un resumen con los valores calculados por el sistema y un botón Confirmar: explícale en una o dos frases qué hará, sin escribir valores en dinero, y pídele que la revise y la confirme. Tú no la ejecutas.";

    [KernelFunction(clsNombresHerramientas.ConsultarCotizacion)]
    [Description("Consulta la cotización de esta conversación: estado del costeo y del envío, cliente, línea, ruta, moneda, márgenes (actual y sugerido, como fracción) y servicios conexos (nombre, cantidad, si van en el PDF). Nunca trae valores en dinero.")]
    public async Task<clsResultadoHerramienta> ConsultarCotizacionAsync(CancellationToken cancellationToken = default)
    {
        var cotizacion = contexto.Cotizacion;
        var lineas = await cotizaciones.ListarLineasAsync(cotizacion.Id, cancellationToken) ?? [];
        var salida = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["folio"] = cotizacion.Folio,
            ["version"] = cotizacion.Version,
            ["estado_costeo"] = cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
            ["estado_envio"] = cotizacion.EstadoEnvio is { } envio ? clsCodigoEnum.ACodigo(envio) : null,
            ["tiene_resultado"] = cotizacion.Ganada is not null,
            ["tiene_pdf"] = cotizacion.PdfPath is not null,
            ["pdf_desactualizado"] = cotizacion.PdfDesactualizado,
            ["cliente"] = cotizacion.ClienteNombre,
            ["linea_negocio"] = contexto.LineaNegocio,
            ["origen"] = cotizacion.Origen,
            ["destino"] = cotizacion.Destino,
            ["tipo_vehiculo"] = cotizacion.TipoVehiculo,
            ["moneda"] = cotizacion.Moneda ?? clsCotizacion.MonedaPesos,
            ["margen_actual"] = cotizacion.MargenAplicado,
            ["margen_sugerido"] = cotizacion.SugeridoVigente,
            ["origen_margen"] = cotizacion.OrigenMargen is { } origen ? clsCodigoEnum.ACodigo(origen) : null,
            ["conexos"] = clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos)
                .Select(c => new Dictionary<string, object?>(StringComparer.Ordinal) { ["nombre"] = c.Nombre, ["cantidad"] = c.Cantidad, ["incluido"] = c.Incluido })
                .ToList(),
        };
        if (lineas.Count > 1)
        {
            // Licitación o varias líneas: cada ruta con su orden (para proponer un conexo a una línea).
            salida["rutas"] = lineas.OrderBy(l => l.Orden)
                .Select(l => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["linea_orden"] = l.Orden,
                    ["origen"] = l.Origen,
                    ["destino"] = l.Destino,
                    ["tipo_vehiculo"] = l.TipoVehiculo,
                })
                .ToList();
        }

        return clsResultadoHerramienta.Exito(clsNombresHerramientas.ConsultarCotizacion, salida);
    }

    [KernelFunction(clsNombresHerramientas.ProponerAjusteMargen)]
    [Description("Propone ajustar el margen de esta cotización. No lo aplica: el comercial lo confirma con un botón. El margen va como fracción (0.18 = 18 %), entre 0 y 0.99. Por debajo del margen sugerido la justificación es obligatoria: pídesela al comercial antes de proponer.")]
    public async Task<clsResultadoHerramienta> ProponerAjusteMargenAsync(
        [Description("Margen nuevo como fracción (0.18 = 18 %).")] decimal margen,
        [Description("Justificación del comercial (obligatoria si el margen queda por debajo del sugerido).")] string? justificacion = null,
        CancellationToken cancellationToken = default)
    {
        var herramienta = clsNombresHerramientas.ProponerAjusteMargen;
        var cotizacion = contexto.Cotizacion;
        var lineas = await cotizaciones.ListarLineasAsync(cotizacion.Id, cancellationToken) ?? [];
        var propuesta = clsPropuestasAsistente.AjustarMargen(precios, cotizacion, lineas, margen, justificacion);
        if (propuesta.EsFallo)
        {
            return clsResultadoHerramienta.Fallo(herramienta, SinDatos(propuesta.Error));
        }

        contexto.Proponer(propuesta.Valor);
        return clsResultadoHerramienta.Exito(herramienta, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["propuesta"] = clsAccionPropuesta.TipoAjustarMargen,
            ["margen_actual"] = cotizacion.MargenAplicado,
            ["margen_propuesto"] = propuesta.Valor.Cuerpo["margen"],
            ["margen_sugerido"] = cotizacion.SugeridoVigente,
            ["con_justificacion"] = propuesta.Valor.Cuerpo["justificacion"] is not null,
            ["mensaje"] = MensajePropuesta,
        });
    }

    [KernelFunction(clsNombresHerramientas.ProponerReSolicitud)]
    [Description("Propone volver a pedir el costeo a Nexo (botón 'Cotizar costo'). No lo ejecuta: el comercial lo confirma con un botón. Sin cambios de carga se pide en el mismo folio (solo con el costeo vencido, entregado o con error de envío); con cambios de carga (peso, horas de cargue o descargue, exclusivo, consolidado) o nuevaVersion=true se crea una versión nueva. Otros cambios (vehículo, carrocería, origen, destino) se hacen desde el detalle de la cotización.")]
    public async Task<clsResultadoHerramienta> ProponerReSolicitudAsync(
        [Description("Por qué se vuelve a pedir el costeo, con las palabras del comercial (1 a 300 caracteres).")] string motivo,
        [Description("Peso nuevo de la carga en kilogramos, si cambia.")] decimal? pesoKg = null,
        [Description("Horas de cargue nuevas, si cambian.")] decimal? horasCargue = null,
        [Description("Horas de descargue nuevas, si cambian.")] decimal? horasDescargue = null,
        [Description("true si ahora el vehículo es exclusivo, false si deja de serlo; omítelo si no cambia.")] bool? exclusivo = null,
        [Description("true si ahora la carga es consolidada, false si deja de serlo; omítelo si no cambia.")] bool? consolidado = null,
        [Description("true para crear una versión nueva aunque no cambie la carga (cotización ya enviada al cliente).")] bool nuevaVersion = false,
        CancellationToken cancellationToken = default)
    {
        var herramienta = clsNombresHerramientas.ProponerReSolicitud;
        var cotizacion = contexto.Cotizacion;
        var cambios = new clsCambiosCargaAsistente(pesoKg, horasCargue, horasDescargue, exclusivo, consolidado);
        var entrada = clsPropuestasAsistente.EntradaReSolicitud(motivo, cambios, nuevaVersion);

        // Las MISMAS validaciones del endpoint (estado, catálogo, rangos, peso frente a las posiciones): si no pasa, no hay propuesta y el
        // modelo explica el motivo (por ejemplo, que el peso no cuadra con las posiciones y hay que ajustarlo desde el detalle).
        if (await reSolicitud.ValidarAsync(contexto.UsuarioId, cotizacion, entrada, cancellationToken) is { } invalida)
        {
            return clsResultadoHerramienta.Fallo(herramienta, SinDatos(invalida));
        }

        var propuesta = clsPropuestasAsistente.ReSolicitar(cotizacion, entrada, cambios);
        contexto.Proponer(propuesta);
        return clsResultadoHerramienta.Exito(herramienta, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["propuesta"] = clsAccionPropuesta.TipoReSolicitarCosteo,
            ["version_nueva"] = propuesta.Cuerpo["nueva_version"],
            ["cambios_de_carga"] = propuesta.Cuerpo.TryGetValue("datos_carga", out var datos) && datos is IReadOnlyDictionary<string, object?> campos
                ? campos.Keys.ToList()
                : [],
            ["mensaje"] = MensajePropuesta,
        });
    }

    [KernelFunction(clsNombresHerramientas.BuscarServicioConexo)]
    [Description("Busca servicios conexos en el catálogo de Nexo (por ejemplo almacenamiento, cargue, precinto) y devuelve id, nombre, categoría y unidad, nunca valores. Si el comercial pidió agregarlo e indicó la cantidad, pásala: con un único servicio que coincida (o el servicioId elegido de una búsqueda anterior) arma la propuesta para agregarlo, que el comercial confirma con un botón. Con varios resultados y sin servicioId, muéstralos y pregunta cuál; después llama de nuevo con el mismo texto y su servicioId.")]
    public async Task<clsResultadoHerramienta> BuscarServicioConexoAsync(
        [Description("Texto a buscar en el nombre o el código del servicio (hasta 100 caracteres).")] string texto,
        [Description("Cantidad a agregar (0.01 a 999999.99), solo si el comercial pidió agregarlo.")] decimal? cantidad = null,
        [Description("Id del servicio elegido de una búsqueda anterior, si había varios.")] string? servicioId = null,
        [Description("Línea de la cotización (1 en una cotización del formulario).")] int lineaOrden = 1,
        CancellationToken cancellationToken = default)
    {
        var herramienta = clsNombresHerramientas.BuscarServicioConexo;
        var pagina = await conexos.BuscarAsync(contexto.UsuarioId, texto, null, 1, MaximoServicios, cancellationToken);
        if (pagina.EsFallo)
        {
            return clsResultadoHerramienta.Fallo(herramienta, SinDatos(pagina.Error));
        }

        var servicios = pagina.Valor.Items;
        var salida = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["total"] = pagina.Valor.Total,
            ["servicios"] = servicios
                .Select(s => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = s.Id,
                    ["nombre"] = s.Nombre,
                    ["codigo"] = s.Codigo,
                    ["categoria"] = s.Categoria,
                    ["unidad"] = s.Unidad,
                })
                .ToList(),
            ["propuesta"] = null,
        };
        if (cantidad is not { } valor)
        {
            return clsResultadoHerramienta.Exito(herramienta, salida);
        }

        var elegido = Elegir(servicios, servicioId);
        if (elegido is null)
        {
            salida["mensaje"] = servicios.Count == 0
                ? "No hay servicios activos con ese texto en el catálogo de Nexo: pide otro nombre."
                : "Hay varios servicios posibles: muéstralos y pregunta cuál agregar; después vuelve a llamar con su servicioId.";
            return clsResultadoHerramienta.Exito(herramienta, salida);
        }

        var cotizacion = contexto.Cotizacion;
        var lineas = await cotizaciones.ListarLineasAsync(cotizacion.Id, cancellationToken) ?? [];
        var propuesta = clsPropuestasAsistente.AgregarConexo(cotizacion, lineas, elegido, valor, lineaOrden);
        if (propuesta.EsFallo)
        {
            return clsResultadoHerramienta.Fallo(herramienta, SinDatos(propuesta.Error));
        }

        contexto.Proponer(propuesta.Valor);
        salida["propuesta"] = clsAccionPropuesta.TipoAgregarServiciosConexos;
        salida["servicio_propuesto"] = elegido.Id;
        salida["mensaje"] = MensajePropuesta;
        return clsResultadoHerramienta.Exito(herramienta, salida);
    }

    /// <summary>
    /// El servicio a proponer: el de <paramref name="servicioId"/> si está en la página; si no, el único resultado. <c>null</c> si hay
    /// varios (el comercial elige: nunca se adivina entre "Precinto" y "Precinto electrónico") o ninguno.
    /// </summary>
    public static clsServicioConexoCatalogo? Elegir(IReadOnlyList<clsServicioConexoCatalogo> servicios, string? servicioId)
    {
        ArgumentNullException.ThrowIfNull(servicios);
        if (!string.IsNullOrWhiteSpace(servicioId))
        {
            return servicios.FirstOrDefault(s => string.Equals(s.Id, servicioId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return servicios.Count == 1 ? servicios[0] : null;
    }

    /// <summary>
    /// Error para el modelo sin <c>Datos</c>: el detalle estructurado de algunos errores (la llamada de la versión nueva de
    /// <c>PDF_YA_ENVIADO</c>, <c>margen_sugerido</c>...) es para el frontend. El <c>Detalle</c> de los errores de Nexo ya llega reducido
    /// a su código desde el adaptador (<c>clsServiciosConexosNexo.SoloCodigo</c>).
    /// </summary>
    private static clsErrorDominio SinDatos(clsErrorDominio error) => new(error.Codigo, error.Mensaje, error.Detalle);
}
