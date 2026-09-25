using System.Globalization;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.DTOs;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Mensajes y tarjetas de Google Chat (port de <c>orquestador/cards.py</c> y <c>orquestador/alertas.py</c>): previsualización
/// (costo · margen · precio con los botones de control humano), tarjeta del PDF con el enlace firmado y texto simple. La
/// previsualización siempre pide confirmación explícita; el correo nunca se dispara desde aquí.
/// </summary>
public static class clsTarjetasChat
{
    /// <summary>Texto que ancla el punto de control humano (invariante de confirmación).</summary>
    public const string PromptConfirmacion =
        "¿Confirmas para generar el PDF, o quieres ajustar el margen? Responde «Confirmo» o «Ajusta el margen a X%».";

    /// <summary>3294798 → <c>$3.294.798</c>.</summary>
    public static string FormatoCop(decimal valor) => clsFormatoMoneda.Formatear(valor, "COP");

    /// <summary>0.135 → <c>13,5%</c>.</summary>
    public static string FormatoPct(decimal valor) =>
        (valor * 100m).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + "%";

    /// <summary>Tarjeta de previsualización con el resumen y los botones "Confirmar y generar PDF" y "Ajustar margen".</summary>
    public static JsonObject Previsualizacion(decimal costo, decimal margenAplicado, decimal precio, decimal utilidad, decimal? margenPolitica = null)
    {
        var politica = margenPolitica ?? margenAplicado;
        var widgets = new JsonArray { Decorado("Costo", FormatoCop(costo)), Decorado("Margen de política", FormatoPct(politica)) };
        if (margenAplicado != politica)
        {
            widgets.Add(Decorado("Margen aplicado", FormatoPct(margenAplicado)));
        }

        widgets.Add(Decorado("Precio", FormatoCop(precio)));
        widgets.Add(Decorado("Utilidad", FormatoCop(utilidad)));
        widgets.Add(new JsonObject
        {
            ["buttonList"] = new JsonObject
            {
                ["buttons"] = new JsonArray
                {
                    Boton("Confirmar y generar PDF", new JsonObject { ["action"] = new JsonObject { ["function"] = clsEventoChat.AccionConfirmar } }),
                    Boton("Ajustar margen", new JsonObject { ["action"] = new JsonObject { ["function"] = clsEventoChat.AccionAjustarMargen } }),
                },
            },
        });

        return Tarjeta(PromptConfirmacion, "previsualizacion", "Previsualización de cotización", "Revisa costo, margen y precio", widgets);
    }

    /// <summary>Tarjeta con el folio y el botón "Abrir PDF" (URL firmada de vida corta, ADR-011 §3); sin URL, solo el folio.</summary>
    public static JsonObject Pdf(string? folio, string? urlPdf, string? texto = null)
    {
        var widgets = new JsonArray { Decorado("Folio", folio ?? "—") };
        if (!string.IsNullOrWhiteSpace(urlPdf))
        {
            widgets.Add(new JsonObject
            {
                ["buttonList"] = new JsonObject
                {
                    ["buttons"] = new JsonArray { Boton("Abrir PDF", new JsonObject { ["openLink"] = new JsonObject { ["url"] = urlPdf } }) },
                },
            });
        }

        return Tarjeta(string.IsNullOrWhiteSpace(texto) ? "Cotización generada." : texto, "pdf", "Cotización lista", folio ?? string.Empty, widgets);
    }

    public static JsonObject Texto(string texto) => new() { ["text"] = texto };

    /// <summary>
    /// Mensaje según lo que produjo el turno (port de <c>mensaje_para_resultado</c>): PDF generado → tarjeta del PDF; precio
    /// calculado → previsualización; respuesta estructurada (HU-003.10) que pide confirmación con acciones sugeridas → texto con
    /// sus botones; si no, texto. Las salidas con error no cuentan. Con el respaldo del validador (respuesta no validada) no se
    /// muestran botones de control: solo el PDF ya generado, si lo hay, o el texto.
    /// </summary>
    /// <param name="urlPdf">URL firmada del PDF de la salida de <c>generar_pdf</c>.</param>
    public static JsonObject ParaResultado(clsResultadoTurno resultado, Func<clsSalidaPdf, string?> urlPdf)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        ArgumentNullException.ThrowIfNull(urlPdf);

        if (resultado.UltimaSalida<clsSalidaPdf>(clsNombresHerramientas.GenerarPdf) is { } pdf)
        {
            return Pdf(pdf.Folio, urlPdf(pdf), resultado.Texto);
        }

        if (resultado.EsRespaldo)
        {
            return Texto(resultado.Texto);
        }

        if (resultado.UltimaSalida<clsSalidaPrecio>(clsNombresHerramientas.CalcularPrecio) is { } precio)
        {
            var politica = resultado.UltimaSalida<clsSalidaMargen>(clsNombresHerramientas.ObtenerMargen)?.Margen;
            return Previsualizacion(precio.Costo, precio.Margen, precio.Precio, precio.Utilidad, politica);
        }

        if (resultado.Respuesta is { RequiereConfirmacion: true, AccionesSugeridas.Count: > 0 } respuesta)
        {
            return ConAcciones(resultado.Texto, respuesta.AccionesSugeridas);
        }

        return Texto(resultado.Texto);
    }

    /// <summary>
    /// Texto con los botones de las acciones sugeridas (el validador ya comprobó que hay una previsualización pendiente): los mismos
    /// "Confirmar y generar PDF" y "Ajustar margen" de la previsualización.
    /// </summary>
    public static JsonObject ConAcciones(string texto, IReadOnlyList<AccionSugeridaAgente> acciones)
    {
        ArgumentNullException.ThrowIfNull(acciones);
        var botones = new JsonArray();
        foreach (var accion in acciones)
        {
            botones.Add(accion switch
            {
                AccionSugeridaAgente.ConfirmarPdf => Boton("Confirmar y generar PDF", new JsonObject { ["action"] = new JsonObject { ["function"] = clsEventoChat.AccionConfirmar } }),
                _ => Boton("Ajustar margen", new JsonObject { ["action"] = new JsonObject { ["function"] = clsEventoChat.AccionAjustarMargen } }),
            });
        }

        return Tarjeta(texto, "confirmacion", "Confirmación pendiente", "Elige cómo seguir", [new JsonObject { ["buttonList"] = new JsonObject { ["buttons"] = botones } }]);
    }

    /// <summary>Tarjeta de alerta para el space de soporte (port de <c>card_alerta_error</c>).</summary>
    public static JsonObject Alerta(string contexto, string detalle) =>
        Tarjeta($"⚠️ Error del bot cotizador: {contexto}", "alerta_error", "Error del Bot Cotizador", contexto, [Decorado("Detalle", detalle)]);

    private static JsonObject Tarjeta(string texto, string id, string titulo, string subtitulo, JsonArray widgets) => new()
    {
        ["text"] = texto,
        ["cardsV2"] = new JsonArray
        {
            new JsonObject
            {
                ["cardId"] = id,
                ["card"] = new JsonObject
                {
                    ["header"] = new JsonObject { ["title"] = titulo, ["subtitle"] = subtitulo },
                    ["sections"] = new JsonArray { new JsonObject { ["widgets"] = widgets } },
                },
            },
        },
    };

    private static JsonObject Decorado(string etiqueta, string texto) =>
        new() { ["decoratedText"] = new JsonObject { ["topLabel"] = etiqueta, ["text"] = texto } };

    private static JsonObject Boton(string texto, JsonObject alPulsar) => new() { ["text"] = texto, ["onClick"] = alPulsar };
}
