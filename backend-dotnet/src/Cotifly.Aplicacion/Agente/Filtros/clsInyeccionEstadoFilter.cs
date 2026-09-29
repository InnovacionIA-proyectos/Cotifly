using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Municipios.Services;
using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Precio;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Cotifly.Aplicacion.Agente.Filtros;

/// <summary>
/// Filtro de invocación (<see cref="IFunctionInvocationFilter"/>, ADR-003/ADR-004 §4): corre en cada <c>kernel.InvokeAsync</c> y
/// sobrescribe los argumentos con el token y el estado resuelto, así el LLM no propaga identificadores ni transcribe números.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Identidad</b> en todas las herramientas: <c>usuarioId</c> (del token o del usuario resuelto), <c>spaceId</c>,
/// <c>threadId</c>, <c>nitCliente</c>, <c>origenCliente</c>, <c>bpCliente</c>, <c>lineaNegocioId</c>, <c>cotizacionId</c> y
/// <c>folio</c>. Si el LLM mandó otro valor, gana el estado y queda en el log.</item>
/// <item><c>buscar_tarifa</c>, <c>calcular_precio</c>, <c>registrar_ajuste_margen</c> y <c>generar_pdf</c>: con un origen o destino
/// ambiguo en el catálogo de municipios (candidatos sin elegir) se rechazan con <c>CONFIRMACION_REQUERIDA</c> hasta que el comercial
/// elija (HU-005.13). En <c>buscar_tarifa</c>, si el texto del modelo es el municipio ya verificado, se reemplaza por su forma
/// canónica <c>Municipio, Departamento</c>; en <c>generar_pdf</c>, el origen de la tabla de tarifa y el destino de su única fila también
/// salen de los municipios verificados.</item>
/// <item><c>obtener_margen</c>: el cliente de la consulta SAP TM es el BP del estado; sin BP no se consulta SAP (nunca con el NIT ni
/// con el texto del LLM).</item>
/// <item><c>calcular_precio</c>: margen y método del estado (sin margen resuelto, <c>PRECIO_INVALIDO</c>); el costo, si vino de
/// <c>buscar_tarifa</c>. Un costo que escribió el comercial en el chat sí llega del mensaje.</item>
/// <item><c>registrar_ajuste_margen</c>: margen de referencia (el propuesto por <c>obtener_margen</c>), costo y método del estado.</item>
/// <item><c>calcular_precio</c> y <c>registrar_ajuste_margen</c>, moneda (HU-012.2): la del selector del formulario web si vino en el turno;
/// si no, la del estado, que reconoce la moneda nombrada en cualquier mensaje del comercial (COP por defecto). La TRM nunca es un
/// argumento: la consulta el servidor.</item>
/// <item><c>generar_pdf</c>: exige la confirmación explícita del turno y un cliente sin candidatos pendientes
/// (<c>CONFIRMACION_REQUERIDA</c>), un precio calculado y, en USD, una TRM vigente hoy (si no, <c>CONFIRMACION_REQUERIDA</c>: se recalcula); reescribe el payload: línea canónica, método, cliente confirmado, y en
/// cada fila tarifa = precio final, costo y margen del estado, moneda de la cotización; quita la <c>referencia</c> (el folio lo
/// genera el servidor), la trazabilidad y los servicios conexos (sus precios vendrán del costeo de Nexo, HU-005).</item>
/// <item><c>enviar_correo</c>: exige la confirmación del turno o que el comercial haya escrito los destinatarios en su mensaje.</item>
/// </list>
/// </remarks>
public sealed class clsInyeccionEstadoFilter(clsContextoTurnoAgente contexto, ILogger<clsInyeccionEstadoFilter> logger) : IFunctionInvocationFilter
{
    private static readonly string[] _trazabilidadDelPayload =
        ["referencia", "costo", "margen_politica", "margen_aplicado", "precio_final", "fuente_tarifa", "origen_margen", "cliente_id_sap", "cliente_id", "servicios_conexos"];

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var estado = contexto.Estado;
        var herramienta = context.Function.Name;
        var args = context.Arguments;

        Sobrescribir(args, herramienta, clsArgumentosHerramienta.UsuarioId, contexto.UsuarioId);
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.SpaceId, contexto.SpaceId);
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.ThreadId, contexto.ThreadId);
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.NitCliente, estado.ClienteConfirmado ? estado.NitCliente : null);
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.OrigenCliente, estado.ClienteConfirmado && estado.OrigenCliente is { } o ? clsCodigoEnum.ACodigo(o) : null);
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.BpCliente, estado.ClienteConfirmado ? estado.BpCliente : null);
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.LineaNegocioId, estado.LineaNegocioId);
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.CotizacionId, estado.CotizacionId);
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.Folio, estado.FolioEnProgreso);

        var rechazo = MunicipioPorConfirmar(herramienta, estado) ?? herramienta switch
        {
            clsNombresHerramientas.BuscarTarifa => InyectarRuta(args, herramienta, estado),
            clsNombresHerramientas.ObtenerMargen => InyectarMargen(args, herramienta, estado),
            clsNombresHerramientas.CalcularPrecio => InyectarPrecio(args, herramienta, estado),
            clsNombresHerramientas.RegistrarAjusteMargen => InyectarAjuste(args, herramienta, estado),
            clsNombresHerramientas.GenerarPdf => InyectarPdf(args, herramienta, estado),
            clsNombresHerramientas.EnviarCorreo => ValidarCorreo(args, herramienta),
            _ => null,
        };

        if (rechazo is not null)
        {
            logger.LogInformation("El orquestador rechazó {Herramienta} antes de ejecutarla: {Codigo}.", herramienta, rechazo.Codigo);
            context.Result = new FunctionResult(context.Function, clsResultadoHerramienta.Fallo(herramienta, rechazo));
            return;
        }

        await next(context);
    }

    /// <summary>
    /// Origen o destino ambiguo sin elegir (HU-005.13): no se cotiza (tarifa, precio, ajuste ni PDF) hasta que el comercial elija el
    /// municipio. Mismo patrón que el cliente con candidatos pendientes: nunca se asume.
    /// </summary>
    private static clsErrorDominio? MunicipioPorConfirmar(string herramienta, clsEstadoConversacion estado)
    {
        if (!estado.TieneMunicipiosPorConfirmar()
            || herramienta is not (clsNombresHerramientas.BuscarTarifa or clsNombresHerramientas.CalcularPrecio
                or clsNombresHerramientas.RegistrarAjusteMargen or clsNombresHerramientas.GenerarPdf))
        {
            return null;
        }

        var pendientes = new List<string>();
        if (estado.CandidatosOrigen.Count > 0)
        {
            pendientes.Add($"el origen «{estado.Origen}»");
        }

        if (estado.CandidatosDestino.Count > 0)
        {
            pendientes.Add($"el destino «{estado.Destino}»");
        }

        return new clsErrorDominio(
            clsCodigosError.ConfirmacionRequerida,
            $"Hay más de un municipio para {string.Join(" y ", pendientes)}: muestra los candidatos (municipio y departamento) y pide al comercial que elija uno antes de cotizar.");
    }

    /// <summary><c>buscar_tarifa</c>: el origen y el destino verificados en el catálogo van en su forma canónica.</summary>
    private clsErrorDominio? InyectarRuta(KernelArguments args, string herramienta, clsEstadoConversacion estado)
    {
        Canonizar(args, herramienta, clsArgumentosHerramienta.Origen, estado.OrigenMunicipio);
        Canonizar(args, herramienta, clsArgumentosHerramienta.Destino, estado.DestinoMunicipio);
        return null;
    }

    private void Canonizar(KernelArguments args, string herramienta, string clave, clsMunicipioResuelto? municipio)
    {
        if (municipio is null)
        {
            return;
        }

        var texto = clsResolvedorMunicipios.Clave(clsArgumentosHerramienta.Texto(args, clave));
        var nombre = clsResolvedorMunicipios.Clave(municipio.Municipio);
        if (texto.Length == 0 || texto == nombre || texto == clsResolvedorMunicipios.Clave(municipio.Canonico()))
        {
            Sobrescribir(args, herramienta, clave, municipio.Canonico());
        }
    }

    private clsErrorDominio? InyectarMargen(KernelArguments args, string herramienta, clsEstadoConversacion estado)
    {
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.Cliente, estado.ClienteConfirmado ? estado.BpCliente : null);
        return null;
    }

    private clsErrorDominio? InyectarPrecio(KernelArguments args, string herramienta, clsEstadoConversacion estado)
    {
        if (estado.MargenAplicado is not { } margen)
        {
            return new clsErrorDominio(
                clsCodigosError.PrecioInvalido,
                "Falta el margen: obtén primero el margen con obtener_margen (el precio siempre parte del margen de la política o de SAP TM).");
        }

        if (estado.Costo is { } costo && estado.FuenteCosto is { } fuente && fuente != clsEstadoConversacion.FuenteCostoManual)
        {
            Sobrescribir(args, herramienta, clsArgumentosHerramienta.Costo, costo);
        }
        else if (clsArgumentosHerramienta.Decimal(args, clsArgumentosHerramienta.Costo) is { } costoDelLlm
                 && costoDelLlm != estado.Costo
                 && !clsNumerosDelMensaje.Contiene(contexto.MensajeUsuario, costoDelLlm))
        {
            // Auditoría de #12 (riesgo 5): un costo manual solo vale si el comercial lo escribió en este turno (o es el que ya dio antes).
            logger.LogInformation("{Herramienta}: costo que el comercial no escribió en su mensaje.", herramienta);
            return new clsErrorDominio(
                clsCodigosError.PrecioInvalido,
                "El costo debe darlo el comercial: pídele el costo del flete (no lo tomes de otra fuente ni lo estimes).");
        }

        Sobrescribir(args, herramienta, clsArgumentosHerramienta.Margen, margen);
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.Metodo, estado.MetodoCalculo ?? "margen");
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.Moneda, MonedaDelTurno(estado));
        return null;
    }

    private clsErrorDominio? InyectarAjuste(KernelArguments args, string herramienta, clsEstadoConversacion estado)
    {
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.MargenPolitica, estado.MargenSugerido ?? estado.MargenPolitica);
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.Costo, estado.Costo);
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.Metodo, estado.MetodoCalculo ?? "margen");
        Sobrescribir(args, herramienta, clsArgumentosHerramienta.Moneda, MonedaDelTurno(estado));
        return null;
    }

    /// <summary>
    /// Moneda del precio (HU-012.2): la del selector del formulario si vino en el turno; si no, la del estado, que ya reconoció la moneda
    /// nombrada en cualquier mensaje del comercial (<c>clsEstadoConversacionService.ReconocerMensaje</c>, ADR-004). Lo que pida el modelo
    /// no cuenta: no convierte a dólares por su cuenta.
    /// </summary>
    private string MonedaDelTurno(clsEstadoConversacion estado) =>
        contexto.MonedaDelFormulario
        ?? (string.IsNullOrWhiteSpace(estado.Moneda) ? clsConversorMoneda.Pesos : estado.Moneda.Trim().ToUpperInvariant());

    private clsErrorDominio? InyectarPdf(KernelArguments args, string herramienta, clsEstadoConversacion estado)
    {
        if (estado.CandidatosCliente.Count > 0 && !estado.ClienteConfirmado)
        {
            return new clsErrorDominio(
                clsCodigosError.ConfirmacionRequerida,
                "El cliente no está confirmado: pide al comercial que elija uno de los clientes candidatos antes de generar el PDF.");
        }

        if (estado.PrecioFinal is not { } precioFinal)
        {
            return new clsErrorDominio(clsCodigosError.PdfPayloadInvalido, "Falta calcular el precio con calcular_precio antes de generar el PDF.");
        }

        if (contexto.Hoy is { } hoy && estado.TrmVencidaEn(hoy))
        {
            return new clsErrorDominio(
                clsCodigosError.ConfirmacionRequerida,
                "La TRM con que se calculó el precio en dólares ya no está vigente: recalcula el precio con calcular_precio y pide una nueva confirmación.");
        }

        if (!contexto.ConfirmadoEnEsteTurno)
        {
            return new clsErrorDominio(
                clsCodigosError.ConfirmacionRequerida,
                "El PDF exige la confirmación explícita del comercial: muestra la previsualización y pide que responda «Confirmo».");
        }

        if (!args.TryGetValue(clsArgumentosHerramienta.Cotizacion, out var valor) || LeerObjeto(valor) is not { } payload)
        {
            // Sin un objeto legible no hay nada que reescribir: el caso de uso responde PDF_PAYLOAD_INVALIDO.
            return null;
        }

        foreach (var campo in _trazabilidadDelPayload)
        {
            payload.Remove(campo);
        }

        if (estado.LineaNegocio is not null)
        {
            payload["linea_negocio"] = estado.LineaNegocio;
        }

        if (estado.MetodoCalculo is not null)
        {
            payload["metodo_calculo"] = estado.MetodoCalculo;
        }

        if (estado.ClienteConfirmado && estado.ClienteNombre is not null)
        {
            payload["cliente"] = estado.ClienteNombre;
        }
        else if (payload["cliente"] is null && estado.ClienteNombre is not null)
        {
            payload["cliente"] = estado.ClienteNombre;
        }

        // Origen y destino verificados en el catálogo de municipios (HU-005.13): el origen de la tabla y, con una sola fila, su destino.
        if (estado.OrigenMunicipio is { } municipioOrigen && payload["tarifa"] is JsonObject tablaOrigen)
        {
            tablaOrigen["origen"] = municipioOrigen.Canonico();
        }

        if (estado.DestinoMunicipio is { } municipioDestino && payload["tarifa"]?["filas"] is JsonArray { Count: 1 } unaFila && unaFila[0] is JsonObject filaDestino)
        {
            filaDestino["destino"] = municipioDestino.Canonico();
        }

        var moneda = estado.Moneda ?? "COP";
        var filas = 0;
        foreach (var fila in Filas(payload))
        {
            filas++;
            fila["tarifa"] = JsonValue.Create(precioFinal);
            fila["costo"] = estado.Costo is { } costo ? JsonValue.Create(costo) : null;
            fila["margen"] = estado.MargenAplicado is { } margen ? JsonValue.Create(margen) : null;
            fila["moneda"] = moneda;
        }

        logger.LogInformation("generar_pdf: el orquestador reescribió {Filas} filas con el precio {Precio} del estado.", filas, precioFinal.ToString(CultureInfo.InvariantCulture));
        args[clsArgumentosHerramienta.Cotizacion] = JsonSerializer.SerializeToElement(payload, clsJsonAplicacion.SnakeCase);
        return null;
    }

    private clsErrorDominio? ValidarCorreo(KernelArguments args, string herramienta)
    {
        // Sin confirmación, los destinatarios deben estar en el mensaje de este turno; con confirmación, en algún mensaje del comercial en
        // la conversación. Nunca de datos externos (auditoría de #12, riesgo 5).
        var destinatarios = clsArgumentosHerramienta.Lista(args, "para").Concat(clsArgumentosHerramienta.Lista(args, "cc")).ToList();
        var fuentes = contexto.ConfirmadoEnEsteTurno ? contexto.MensajesDelComercial : [contexto.MensajeUsuario];
        if (destinatarios.Count > 0 && destinatarios.All(d => fuentes.Any(m => m.Contains(d, StringComparison.OrdinalIgnoreCase))))
        {
            return null;
        }

        logger.LogInformation("{Herramienta}: destinatarios que el comercial no escribió en su mensaje y sin confirmación.", herramienta);
        return new clsErrorDominio(
            clsCodigosError.ConfirmacionRequerida,
            "El envío exige que el comercial escriba los correos de los destinatarios (y lo pida o lo confirme de forma explícita).");
    }

    private static IEnumerable<JsonObject> Filas(JsonObject payload)
    {
        var tablas = new List<JsonNode?> { payload["tarifa"] };
        if (payload["secciones"] is JsonArray secciones)
        {
            tablas.AddRange(secciones);
        }

        foreach (var tabla in tablas.OfType<JsonObject>())
        {
            if (tabla["filas"] is JsonArray filas)
            {
                foreach (var fila in filas.OfType<JsonObject>())
                {
                    yield return fila;
                }
            }
        }
    }

    private static JsonObject? LeerObjeto(object? valor)
    {
        try
        {
            return valor switch
            {
                JsonElement { ValueKind: JsonValueKind.Object } elemento => JsonNode.Parse(elemento.GetRawText()) as JsonObject,
                JsonElement { ValueKind: JsonValueKind.String } elemento => JsonNode.Parse(elemento.GetString() ?? string.Empty) as JsonObject,
                string texto => JsonNode.Parse(texto) as JsonObject,
                JsonObject objeto => JsonNode.Parse(objeto.ToJsonString()) as JsonObject,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Pone el valor del estado; si el LLM había mandado otro, deja constancia en el log (sin valores sensibles).</summary>
    private void Sobrescribir(KernelArguments args, string herramienta, string clave, object? valor)
    {
        if (args.TryGetValue(clave, out var anterior) && anterior is not null && !Iguales(anterior, valor))
        {
            logger.LogInformation("{Herramienta}: el argumento '{Clave}' que mandó el modelo se reemplazó por el del estado.", herramienta, clave);
        }

        args[clave] = valor;
    }

    private static bool Iguales(object anterior, object? valor)
    {
        if (valor is null)
        {
            return false;
        }

        var texto = anterior is JsonElement elemento
            ? elemento.ValueKind == JsonValueKind.String ? elemento.GetString() : elemento.GetRawText()
            : Convert.ToString(anterior, CultureInfo.InvariantCulture);
        var nuevo = Convert.ToString(valor, CultureInfo.InvariantCulture);
        if (decimal.TryParse(texto, NumberStyles.Number, CultureInfo.InvariantCulture, out var a)
            && decimal.TryParse(nuevo, NumberStyles.Number, CultureInfo.InvariantCulture, out var b))
        {
            return a == b;
        }

        return string.Equals(texto?.Trim(), nuevo?.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
