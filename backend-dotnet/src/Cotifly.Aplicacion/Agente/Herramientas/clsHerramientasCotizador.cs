using System.ComponentModel;
using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Aplicacion.Municipios.Services;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Aplicacion.Tarifas.DTOs;
using Cotifly.Aplicacion.Tarifas.Interfaces;
using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Cotifly.Aplicacion.Agente.Herramientas;

/// <summary>
/// Plugin <c>cotizador</c> de Semantic Kernel (ADR-003): las herramientas deterministas del agente, port de las del MCP
/// (<c>mcp_server/server.py</c>) sobre los casos de uso de #10, #11 y #12. El modelo solo ve la declaración (nombre, descripción y
/// esquema); cada llamada pasa por <c>kernel.InvokeAsync</c> y por los filtros (inyección de estado y auditoría).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Firmas unificadas del plan (parámetros camelCase). Los identificadores (comercial, hilo, NIT, BP, cotización, folio) no son
/// parámetros: los pone el filtro en <see cref="KernelArguments"/> desde el token y el estado (ADR-004 §4).</item>
/// <item>Cada herramienta devuelve <see cref="clsResultadoHerramienta"/>: la salida del contrato o el error estructurado.</item>
/// </list>
/// </remarks>
public sealed class clsHerramientasCotizador(
    clsContextoTurnoAgente contexto,
    IClsBuscadorClientes buscadorClientes,
    IClsMunicipiosProvider municipios,
    IClsTarifaProvider tarifas,
    IClsMargenResolverService margen,
    IClsPrecioService precio,
    IClsAjusteMargenService ajusteMargen,
    IClsGenerarPdfService pdf,
    IClsGeneradorFolioService folios,
    IClsCorreoService correo,
    IClsCotizacionRepository cotizaciones,
    IOptions<clsOpcionesPdf> opcionesPdf)
{
    /// <summary>Máximo de resultados de <c>consultar_cotizacion</c>.</summary>
    public const int MaximoConsulta = 20;

    [KernelFunction(clsNombresHerramientas.BuscarCliente)]
    [Description("Busca al cliente en el catálogo (clientes y prospectos) con el texto que escribió el comercial. Devuelve si hubo coincidencia exacta y los candidatos; si hay varios, pide al comercial que confirme cuál es.")]
    public async Task<clsResultadoHerramienta> BuscarClienteAsync(
        [Description("Nombre o NIT del cliente tal como lo escribió el comercial.")] string nombre,
        KernelArguments? argumentos = null,
        CancellationToken cancellationToken = default)
    {
        var herramienta = clsNombresHerramientas.BuscarCliente;
        if (string.IsNullOrWhiteSpace(nombre))
        {
            return clsResultadoHerramienta.Fallo(herramienta, clsCodigosError.ClienteBusquedaNoDisponible, "Falta el nombre o NIT del cliente que se busca.");
        }

        var resultado = await buscadorClientes.BuscarAsync(UsuarioDe(Argumentos(argumentos)), nombre.Trim(), cancellationToken);
        if (resultado.EsFallo)
        {
            return clsResultadoHerramienta.Fallo(herramienta, resultado.Error);
        }

        var busqueda = resultado.Valor;

        // El BP no va al modelo: solo vive en el estado para la consulta de SAP TM (ADR-005).
        var salida = new
        {
            exacta = busqueda.Exacta && busqueda.Candidatos.Count == 1,
            candidatos = busqueda.Candidatos.Select(c => new { nit_cliente = c.NitCliente, nombre = c.Nombre, origen_cliente = c.OrigenCliente }).ToList(),
        };
        return clsResultadoHerramienta.Exito(herramienta, salida, busqueda);
    }

    [KernelFunction(clsNombresHerramientas.ResolverMunicipio)]
    [Description("Resuelve el origen o el destino que escribió el comercial contra el catálogo de municipios de Nexo. Llámala antes de buscar_tarifa con cada ciudad nueva. coincidencia 'unica': usa ese municipio; 'varias': muestra los candidatos (municipio y departamento) y pregunta cuál es antes de seguir; 'ninguna': dilo y pide que la escriba de nuevo, nunca inventes un municipio. Si el catálogo no está disponible, sigue con el texto del comercial.")]
    public async Task<clsResultadoHerramienta> ResolverMunicipioAsync(
        [Description("Ciudad o municipio tal como lo escribió el comercial; puede incluir el departamento (ej. 'La Unión, Nariño').")] string texto,
        [Description("origen o destino.")] string campo,
        KernelArguments? argumentos = null,
        CancellationToken cancellationToken = default)
    {
        var herramienta = clsNombresHerramientas.ResolverMunicipio;

        // Argumentos inválidos: error para que el modelo corrija la llamada, sin consultar Nexo.
        if (clsArgumentosHerramienta.CampoMunicipio(campo) is not { } campoValido)
        {
            return clsResultadoHerramienta.Fallo(
                herramienta, clsCodigosError.MunicipioArgumentoInvalido, "El campo debe ser «origen» o «destino»: vuelve a llamar a resolver_municipio con uno de los dos.");
        }

        if (string.IsNullOrWhiteSpace(texto))
        {
            return clsResultadoHerramienta.Fallo(
                herramienta, clsCodigosError.MunicipioArgumentoInvalido, "Falta el texto del municipio: pásalo tal como lo escribió el comercial.");
        }

        var catalogo = await municipios.ObtenerAsync(UsuarioDe(Argumentos(argumentos)), cancellationToken);
        if (catalogo.EsFallo)
        {
            return clsResultadoHerramienta.Fallo(herramienta, catalogo.Error);
        }

        var resolucion = clsResolvedorMunicipios.Resolver(texto, catalogo.Valor);

        // Al modelo solo van nombres (sin id ni código Dane): ningún número nuevo que respalde un monto en el validador (#16).
        var salida = new
        {
            campo = campoValido,
            texto = resolucion.Texto,
            coincidencia = resolucion.Coincidencia switch
            {
                CoincidenciaMunicipio.Unica => "unica",
                CoincidenciaMunicipio.Varias => "varias",
                _ => "ninguna",
            },
            municipio = resolucion.Resuelto is { } unico ? ParaModelo(unico) : null,
            candidatos = resolucion.Coincidencia == CoincidenciaMunicipio.Varias ? resolucion.Candidatos.Select(ParaModelo).ToList() : [],
            total = resolucion.Total,
        };
        return clsResultadoHerramienta.Exito(herramienta, salida, resolucion);
    }

    [KernelFunction(clsNombresHerramientas.BuscarTarifa)]
    [Description("Busca la tarifa de costo de la ruta. La tarifa depende del peso: pasa pesoKg. Si la fuente no está disponible o no hay tarifa, pide al comercial el costo; nunca inventes una tarifa.")]
    public async Task<clsResultadoHerramienta> BuscarTarifaAsync(
        [Description("Línea de negocio (Nacional, Distribucion, Importacion, Exportacion, Internacional, Almacenamiento, Almacenamiento3PL, ITR).")] string lineaNegocio,
        [Description("Ciudad o punto de origen.")] string origen,
        [Description("Ciudad o punto de destino.")] string destino,
        [Description("Tipo de vehículo, si se conoce.")] string? tipoVehiculo = null,
        [Description("Tipo de carga, si se conoce.")] string? tipoCarga = null,
        [Description("Peso de la carga en kilogramos.")] decimal? pesoKg = null,
        CancellationToken cancellationToken = default)
    {
        var resultado = await tarifas.BuscarAsync(new clsEntradaBuscarTarifa(lineaNegocio, origen, destino, tipoVehiculo, tipoCarga, pesoKg), cancellationToken);
        return resultado.Coincidir(
            t => clsResultadoHerramienta.Exito(clsNombresHerramientas.BuscarTarifa, t),
            e => clsResultadoHerramienta.Fallo(clsNombresHerramientas.BuscarTarifa, e));
    }

    [KernelFunction(clsNombresHerramientas.ObtenerMargen)]
    [Description("Margen propuesto para el cliente y la línea (SAP TM), con respaldo en la política de la línea. Es siempre el punto de partida del precio. Si viene anomalo=true, repórtalo para validación.")]
    public async Task<clsResultadoHerramienta> ObtenerMargenAsync(
        [Description("Línea de negocio.")] string lineaNegocio,
        [Description("Cliente de la cotización (el servidor usa el cliente ya identificado).")] string? cliente = null,
        CancellationToken cancellationToken = default)
    {
        // El filtro reemplaza 'cliente' por el BP del estado (ADR-004): sin BP no se consulta SAP TM, nunca con el NIT ni con texto.
        var resultado = await margen.ResolverAsync(new clsEntradaMargen(lineaNegocio, string.IsNullOrWhiteSpace(cliente) ? null : cliente.Trim()), cancellationToken);
        return resultado.Coincidir(
            m => clsResultadoHerramienta.Exito(clsNombresHerramientas.ObtenerMargen, m.Salida, m),
            e => clsResultadoHerramienta.Fallo(clsNombresHerramientas.ObtenerMargen, e));
    }

    [KernelFunction(clsNombresHerramientas.CalcularPrecio)]
    [Description("Cálculo determinista del precio. margen: precio = costo / (1 - margen); markup: precio = costo * (1 + margen). El margen y el método los pone el servidor desde obtener_margen.")]
    public Task<clsResultadoHerramienta> CalcularPrecioAsync(
        [Description("Costo del flete en pesos (el que dio buscar_tarifa o el comercial).")] decimal costo,
        [Description("Margen como fracción (0.15 = 15 %).")] decimal margen,
        [Description("margen (por defecto) o markup.")] string metodo = "margen")
    {
        var resultado = precio.Calcular(costo, margen, metodo);
        return Task.FromResult(resultado.Coincidir(
            p => clsResultadoHerramienta.Exito(clsNombresHerramientas.CalcularPrecio, p),
            e => clsResultadoHerramienta.Fallo(clsNombresHerramientas.CalcularPrecio, e)));
    }

    [KernelFunction(clsNombresHerramientas.RegistrarAjusteMargen)]
    [Description("Registra el margen que negoció el comercial cuando pide explícitamente un margen distinto al propuesto, y calcula el precio con ese margen. El margen de referencia, el costo y el método los pone el servidor.")]
    public async Task<clsResultadoHerramienta> RegistrarAjusteMargenAsync(
        [Description("Margen negociado como fracción (0.11 = 11 %).")] decimal margenAplicado,
        [Description("Motivo del ajuste, si el comercial lo dio.")] string? motivo = null,
        KernelArguments? argumentos = null,
        CancellationToken cancellationToken = default)
    {
        var herramienta = clsNombresHerramientas.RegistrarAjusteMargen;
        var args = Argumentos(argumentos);
        if (clsArgumentosHerramienta.Decimal(args, clsArgumentosHerramienta.MargenPolitica) is not { } referencia)
        {
            return clsResultadoHerramienta.Fallo(herramienta, clsCodigosError.AjusteMargenInvalido, "Falta el margen propuesto: obtén primero el margen con obtener_margen.");
        }

        var entrada = new clsEntradaAjusteMargen(
            referencia,
            margenAplicado,
            UsuarioDe(args),
            clsArgumentosHerramienta.Texto(args, clsArgumentosHerramienta.Metodo) ?? "margen",
            motivo,
            clsArgumentosHerramienta.Decimal(args, clsArgumentosHerramienta.Costo),
            clsArgumentosHerramienta.Identificador(args, clsArgumentosHerramienta.CotizacionId),
            clsArgumentosHerramienta.Texto(args, clsArgumentosHerramienta.Folio),
            null,
            clsArgumentosHerramienta.Texto(args, clsArgumentosHerramienta.ThreadId));
        var resultado = await ajusteMargen.RegistrarAsync(entrada, cancellationToken);
        return resultado.Coincidir(
            a => clsResultadoHerramienta.Exito(herramienta, SalidaAjuste(a), a, auditadoPorCasoDeUso: true),
            e => clsResultadoHerramienta.Fallo(herramienta, e, auditadoPorCasoDeUso: true));
    }

    [KernelFunction(clsNombresHerramientas.GenerarPdf)]
    [Description("Genera el PDF corporativo de la cotización y la registra. Solo con la confirmación explícita del comercial. El folio, el cliente identificado y los números (costo, margen, tarifa, precio) los pone el servidor; tú aportas los textos del payload.")]
    public async Task<clsResultadoHerramienta> GenerarPdfAsync(
        [Description("Payload del PDF (esquema de la skill: ciudad, fecha, cliente, contactos, linea_negocio, modalidad, textos, comercial.cargo, tarifa{titulo, origen, filas[{destino, vehiculo_peso, nota}]}, aduana, extradimensionada, refrigerada).")] JsonElement cotizacion,
        KernelArguments? argumentos = null,
        CancellationToken cancellationToken = default)
    {
        var herramienta = clsNombresHerramientas.GenerarPdf;
        var args = Argumentos(argumentos);
        var payload = clsCotizacionPdfPayload.DesdeJson(cotizacion.ValueKind == JsonValueKind.String ? cotizacion.GetString() : cotizacion.GetRawText());
        if (payload.EsFallo)
        {
            return clsResultadoHerramienta.Fallo(herramienta, payload.Error);
        }

        var estado = contexto.Estado;
        var usuarioId = UsuarioDe(args);
        var spaceId = clsArgumentosHerramienta.Texto(args, clsArgumentosHerramienta.SpaceId);
        var threadId = clsArgumentosHerramienta.Texto(args, clsArgumentosHerramienta.ThreadId);
        var trazabilidad = Trazabilidad(estado);

        Task<clsResultado<clsSalidaPdf>> Crear(Dominio.Cotizaciones.clsFolio folio, CancellationToken ct) =>
            pdf.GenerarAsync(new clsEntradaGenerarPdf(usuarioId, payload.Valor, folio.Valor, folio.Version, trazabilidad, spaceId, threadId), ct);

        // El folio lo genera el servidor (ADR-011): una cotización nueva, o una versión nueva si en esta conversación ya se generó una.
        var resultado = clsArgumentosHerramienta.Identificador(args, clsArgumentosHerramienta.CotizacionId) is { } anterior
            ? await folios.CrearNuevaVersionAsync(usuarioId, anterior, Crear, cancellationToken)
            : await folios.CrearConFolioNuevoAsync(usuarioId, clsArgumentosHerramienta.Texto(args, clsArgumentosHerramienta.NitCliente), Crear, cancellationToken);
        return resultado.Coincidir(
            p => clsResultadoHerramienta.Exito(herramienta, p, auditadoPorCasoDeUso: true),
            e => clsResultadoHerramienta.Fallo(herramienta, e, auditadoPorCasoDeUso: true));
    }

    [KernelFunction(clsNombresHerramientas.EnviarCorreo)]
    [Description("Envía al cliente la última cotización generada, con el PDF adjunto. Solo si el comercial lo pide de forma explícita, con los destinatarios.")]
    public async Task<clsResultadoHerramienta> EnviarCorreoAsync(
        [Description("Correos de los destinatarios principales.")] List<string> para,
        [Description("Asunto; por defecto 'Cotización {folio} — Botero Soto'.")] string? asunto = null,
        [Description("Cuerpo del correo; por defecto la plantilla corporativa.")] string? cuerpo = null,
        [Description("Correos en copia.")] List<string>? cc = null,
        KernelArguments? argumentos = null,
        CancellationToken cancellationToken = default)
    {
        var herramienta = clsNombresHerramientas.EnviarCorreo;
        var args = Argumentos(argumentos);
        if (clsArgumentosHerramienta.Identificador(args, clsArgumentosHerramienta.CotizacionId) is not { } cotizacionId)
        {
            return clsResultadoHerramienta.Fallo(herramienta, clsCodigosError.CotizacionNoEncontrada, "No hay una cotización generada en esta conversación: genera primero el PDF.");
        }

        var resultado = await correo.EnviarAsync(
            new clsEntradaEnviarCorreo(UsuarioDe(args), para ?? [], cotizacionId, null, cc, asunto, cuerpo, clsArgumentosHerramienta.Texto(args, clsArgumentosHerramienta.ThreadId)),
            cancellationToken);
        return resultado.Coincidir(
            s => clsResultadoHerramienta.Exito(herramienta, s, auditadoPorCasoDeUso: true),
            e => clsResultadoHerramienta.Fallo(herramienta, e, auditadoPorCasoDeUso: true));
    }

    [KernelFunction(clsNombresHerramientas.ConsultarCotizacion)]
    [Description("Busca cotizaciones del comercial por folio o por cliente (solo las suyas), para reutilizar o reenviar una cotización previa.")]
    public async Task<clsResultadoHerramienta> ConsultarCotizacionAsync(
        [Description("Folio completo o parcial.")] string? folio = null,
        [Description("Nombre o NIT del cliente, completo o parcial.")] string? cliente = null,
        KernelArguments? argumentos = null,
        CancellationToken cancellationToken = default)
    {
        var args = Argumentos(argumentos);
        var filas = await cotizaciones.ListarConLineaPorUsuarioAsync(UsuarioDe(args), IClsCotizacionRepository.LimitePorDefecto, cancellationToken);
        var folioBuscado = clsEstadoConversacionService.Normalizar(folio);
        var clienteBuscado = clsEstadoConversacionService.Normalizar(cliente);
        var baseUrl = opcionesPdf.Value.BaseUrl;
        var resultados = filas
            .Where(f => folioBuscado.Length == 0 || clsEstadoConversacionService.Normalizar(f.Cotizacion.Folio).Contains(folioBuscado, StringComparison.Ordinal))
            .Where(f => clienteBuscado.Length == 0
                        || clsEstadoConversacionService.Normalizar(f.Cotizacion.ClienteNombre).Contains(clienteBuscado, StringComparison.Ordinal)
                        || (f.Cotizacion.NitCliente ?? string.Empty).Contains(clienteBuscado, StringComparison.Ordinal))
            .Take(MaximoConsulta)
            .Select(f => clsCotizacionResumen.Desde(f.Cotizacion, f.LineaNegocio, baseUrl))
            .ToList();
        return clsResultadoHerramienta.Exito(clsNombresHerramientas.ConsultarCotizacion, new { total = resultados.Count, resultados });
    }

    /// <summary>Trazabilidad de la cotización desde el estado, nunca desde el payload del LLM (ADR-004, #11).</summary>
    public static clsTrazabilidadCotizacion Trazabilidad(clsEstadoConversacion estado)
    {
        ArgumentNullException.ThrowIfNull(estado);
        return new clsTrazabilidadCotizacion(
            estado.Costo,
            estado.MargenPolitica,
            estado.MargenAplicado,
            estado.OrigenMargen is { } origen ? clsCodigoEnum.ACodigo(origen) : null,
            estado.MetodoCalculo,
            estado.PrecioFinal,
            estado.ClienteConfirmado ? estado.NitCliente : null,
            estado.ClienteConfirmado && estado.OrigenCliente is { } origenCliente ? clsCodigoEnum.ACodigo(origenCliente) : null,
            estado.Moneda,
            0m,
            estado.CosteoId);
    }

    private static object ParaModelo(Dominio.Agente.clsMunicipioResuelto municipio) =>
        new { municipio = municipio.Municipio, departamento = municipio.Departamento, pais = municipio.Pais };

    private static object SalidaAjuste(clsSalidaAjusteMargen ajuste) => new
    {
        margen = ajuste.Margen,
        registrado = ajuste.Registrado,
        diferencia = ajuste.Diferencia,
        precio = ajuste.Precio,
    };

    private Guid UsuarioDe(IReadOnlyDictionary<string, object?> argumentos) =>
        clsArgumentosHerramienta.Identificador(argumentos, clsArgumentosHerramienta.UsuarioId) ?? contexto.UsuarioId;

    private static IReadOnlyDictionary<string, object?> Argumentos(KernelArguments? argumentos) =>
        argumentos is null ? new Dictionary<string, object?>() : argumentos;
}
