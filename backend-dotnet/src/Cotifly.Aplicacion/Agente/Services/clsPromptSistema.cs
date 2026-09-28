using System.Globalization;
using System.Text;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Prompt de sistema del agente: la skill <c>cotizador-botero-soto</c> (port de <c>SYSTEM_COTIZADOR</c> de
/// <c>orquestador/prompt.py</c>, recurso embebido <c>Agente/Recursos/prompt_cotizador.md</c>) precedida de dos bloques:
/// "# Contexto del comercial" (identidad real del comercial y fecha de negocio, port de <c>_system_con_contexto</c>) y
/// "# Estado resuelto" (ADR-004: informativo; la fuente de verdad es el filtro de inyección).
/// </summary>
/// <remarks>
/// Diferencias con el Python: el folio lo genera el servidor (se corrige la contradicción del prompt, que pedía una
/// <c>referencia</c> "nunca PCN" mientras el MCP la usaba como folio <c>PCN-DDMMAAAA</c>), el LLM no escribe números ni
/// identificadores (ADR-003) y el tarifario de Drive ya no existe (el costeo viene de Nexo, HU-005).
/// </remarks>
public static class clsPromptSistema
{
    public const string NombreRecurso = "Cotifly.Agente.PromptCotizador.md";

    private static readonly Lazy<string> _base = new(CargarBase);

    /// <summary>La skill sin los bloques de contexto.</summary>
    public static string Base => _base.Value;

    /// <summary>Prompt completo del turno.</summary>
    /// <param name="nombreComercial">Nombre del comercial (<c>UsuariosWeb.nombreCompleto</c>).</param>
    /// <param name="emailComercial">Correo del comercial (<c>UsuariosWeb.email</c>).</param>
    /// <param name="hoy">Fecha de negocio (America/Bogota).</param>
    /// <param name="estado">Estado resuelto de la conversación.</param>
    public static string Construir(string? nombreComercial, string? emailComercial, DateOnly hoy, clsEstadoConversacion estado)
    {
        ArgumentNullException.ThrowIfNull(estado);
        var texto = new StringBuilder();
        texto.AppendLine("# Contexto del comercial");
        if (!string.IsNullOrWhiteSpace(nombreComercial))
        {
            texto.Append("- Nombre: ").AppendLine(nombreComercial.Trim());
        }

        if (!string.IsNullOrWhiteSpace(emailComercial))
        {
            texto.Append("- Correo: ").AppendLine(emailComercial.Trim());
        }

        texto.Append("- Fecha de hoy: ").AppendLine(hoy.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        texto.AppendLine();
        texto.AppendLine(BloqueEstado(estado));
        texto.AppendLine();
        texto.Append(Base);
        return texto.ToString();
    }

    /// <summary>Bloque "# Estado resuelto": solo los datos que ya resolvieron las herramientas o el comercial.</summary>
    public static string BloqueEstado(clsEstadoConversacion estado)
    {
        ArgumentNullException.ThrowIfNull(estado);
        var lineas = new List<string>();
        if (estado.ClienteNombre is not null || estado.NitCliente is not null)
        {
            var cliente = estado.ClienteNombre ?? "(sin nombre)";
            if (estado.NitCliente is not null)
            {
                var origen = estado.OrigenCliente is { } o ? $", {clsCodigoEnum.ACodigo(o)}" : string.Empty;
                cliente += $" (NIT {estado.NitCliente}{origen})";
                cliente += estado.ClienteConfirmado ? ", confirmado en el catálogo" : ", pendiente de confirmar";
            }
            else
            {
                cliente += " (sin identificar en el catálogo: la cotización queda sin NIT)";
            }

            lineas.Add("- Cliente: " + cliente);
        }

        if (estado.CandidatosCliente.Count > 0 && !estado.ClienteConfirmado)
        {
            lineas.Add("- Clientes candidatos por confirmar: "
                + string.Join("; ", estado.CandidatosCliente.Select((c, i) => $"{i + 1}. {c.Nombre} (NIT {c.NitCliente})")));
        }

        if (estado.LineaNegocio is not null)
        {
            lineas.Add("- Línea de negocio: " + estado.LineaNegocio);
        }

        var ruta = new[] { estado.Origen, estado.Destino }.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (ruta.Count > 0)
        {
            var detalle = string.Join(" → ", ruta);
            if (estado.PesoKg is { } peso)
            {
                detalle += $", {peso.ToString(CultureInfo.InvariantCulture)} kg";
            }

            lineas.Add("- Ruta: " + detalle);
        }

        if (estado.Costo is { } costo)
        {
            var fuente = estado.FuenteCosto == clsEstadoConversacion.FuenteCostoManual ? "lo dio el comercial" : $"fuente {estado.FuenteCosto}";
            lineas.Add($"- Costo: {clsFormatoMoneda.Formatear(costo, "COP")} ({fuente})");
        }

        if (estado.MargenAplicado is { } aplicado)
        {
            var partes = new List<string>();
            if (estado.OrigenMargen is { } origenMargen)
            {
                partes.Add("origen " + clsCodigoEnum.ACodigo(origenMargen));
            }

            if (estado.MargenPolitica is { } politica)
            {
                partes.Add("política de la línea " + Porcentaje(politica));
            }

            if (estado.MetodoCalculo is not null)
            {
                partes.Add("método " + estado.MetodoCalculo);
            }

            lineas.Add($"- Margen aplicado: {Porcentaje(aplicado)}" + (partes.Count > 0 ? $" ({string.Join(", ", partes)})" : string.Empty));
        }

        if (estado.PrecioFinal is { } precio)
        {
            lineas.Add($"- Precio final: {clsFormatoMoneda.Formatear(precio, estado.Moneda)}");
        }

        if (!Dominio.Precio.clsConversorMoneda.EsPesos(estado.Moneda))
        {
            lineas.Add($"- Moneda: {estado.Moneda} (TRM aplicada: {(estado.TrmAplicada is { } trm ? clsFormatoMoneda.FormatearTrm(trm) : "se consulta al calcular el precio")})");
        }

        if (estado.FolioEnProgreso is not null)
        {
            lineas.Add($"- Cotización generada: folio {estado.FolioEnProgreso} (versión {estado.Version ?? 1})");
        }

        if (estado.ConfirmacionPendiente)
        {
            lineas.Add("- Previsualización mostrada: falta la confirmación explícita del comercial para generar el PDF.");
        }

        var bloque = new StringBuilder("# Estado resuelto").AppendLine();
        if (lineas.Count == 0)
        {
            bloque.Append("- Todavía no hay datos resueltos por las herramientas.");
            return bloque.ToString();
        }

        foreach (var linea in lineas)
        {
            bloque.AppendLine(linea);
        }

        bloque.Append("Estos datos los resolvió el servidor: úsalos para redactar; no hace falta repetirlos como argumentos.");
        return bloque.ToString();
    }

    /// <summary>0.135 → <c>13,5 %</c> (hasta 2 decimales).</summary>
    public static string Porcentaje(decimal fraccion) =>
        (fraccion * 100m).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',') + " %";

    private static string CargarBase()
    {
        using var flujo = typeof(clsPromptSistema).Assembly.GetManifestResourceStream(NombreRecurso)
            ?? throw new InvalidOperationException($"No se encontró el recurso embebido {NombreRecurso}.");
        using var lector = new StreamReader(flujo, Encoding.UTF8);
        return lector.ReadToEnd().Trim();
    }
}
