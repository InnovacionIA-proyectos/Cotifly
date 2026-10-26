using System.Collections;
using System.Reflection;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Metricas.DTOs;

namespace Cotifly.Arquitectura.Tests;

/// <summary>
/// HU-039.1 (#218): el costo bruto de la ruta (<see cref="clsRutaCosteo.CostoFinal"/>) solo sale por la vista del costeo del dueño
/// (<c>GET .../costeo</c>, las acciones que la devuelven y el detalle). Reversa acotada de ADR-015 §5: ni el chat o asistente, ni el PDF, ni
/// el correo, ni el Excel de la licitación, ni las métricas, ni "Mis cotizaciones" o el listado regional usan esa vista ni traen un costo
/// propio en su respuesta.
/// </summary>
public sealed class clsCostoBrutoSinFugasTests
{
    /// <summary>Caminos que nunca deben ver la vista del costeo (namespaces de las cuatro capas).</summary>
    private static readonly string[] _caminosSinCosto = ["Agente", "Pdf", "Correo", "Licitaciones", "Metricas", "Redaccion", "Mercancias"];

    private static readonly Type[] _vistasConCosto = [typeof(clsRutaCosteo), typeof(clsCosteoCotizacion)];

    /// <summary>
    /// Campos heredados que existen en el tipo pero nunca se llenan: <c>RespuestaChat.precio</c> (<c>PrecioCalculado</c>, con
    /// <c>costo</c>) se conserva en el contrato por compatibilidad y siempre sale <c>null</c> (ADR-018: el chat ya no cotiza);
    /// <c>asistente_folio.feature</c> lo comprueba ("los campos precio, margen y pdf de la respuesta del chat son null").
    /// </summary>
    private static readonly HashSet<string> _heredadosSiempreNull = new(StringComparer.Ordinal) { "clsRespuestaChat.Precio" };

    [Fact]
    public void NingunCaminoFueraDelCosteoUsaLaVistaQueTraeElCostoBruto()
    {
        var prohibidos = _vistasConCosto.Select(t => t.FullName!).ToHashSet(StringComparer.Ordinal);

        var violaciones = clsCapas.Modelo.Types
            .Where(t => _caminosSinCosto.Any(c => EnCamino(t.Namespace.FullName, c)))
            .SelectMany(t => t.Dependencies.Where(d => prohibidos.Contains(d.Target.FullName)).Select(d => $"{t.FullName} → {d.Target.FullName}"))
            .Distinct()
            .ToList();

        violaciones.Should().BeEmpty("el costo bruto solo sale en la vista del costeo del dueño");
    }

    [Fact]
    public void LaReglaNoEsVaciaElDetalleYElAjusteDeMargenSiUsanLaVista()
    {
        var conVista = clsCapas.Modelo.Types
            .Where(t => t.Dependencies.Any(d => d.Target.FullName == typeof(clsCosteoCotizacion).FullName))
            .Select(t => t.FullName)
            .ToList();

        conVista.Should().Contain(n => n.EndsWith(".clsDetalleCotizacionService", StringComparison.Ordinal))
            .And.Contain(n => n.EndsWith(".clsAjusteMargenCosteoService", StringComparison.Ordinal));
    }

    /// <summary>Las respuestas de los demás caminos no tienen ningún campo de costo (<c>Costo*</c>) en todo su árbol.</summary>
    [Theory]
    [InlineData(typeof(clsMisCotizaciones))]
    [InlineData(typeof(clsCotizacionesRegional))]
    [InlineData(typeof(clsVistaPreviaCorreo))]
    [InlineData(typeof(clsResumenCorreo))]
    [InlineData(typeof(clsRespuestaChat))]
    [InlineData(typeof(clsLicitacion))]
    [InlineData(typeof(clsExportacionLicitacion))]
    [InlineData(typeof(clsMetricasComercial))]
    [InlineData(typeof(clsMetricasRegional))]
    public void LasRespuestasDeLosDemasCaminosNoTienenCampoDeCosto(Type respuesta)
    {
        CamposConCosto(respuesta, respuesta.Name, []).Should().BeEmpty($"{respuesta.Name} no expone costos de Nexo");
    }

    [Fact]
    public void LaRutaDelCosteoSoloTraeElCostoBrutoSinDesglose()
    {
        CamposConCosto(typeof(clsRutaCosteo), nameof(clsRutaCosteo), []).Should().Equal($"{nameof(clsRutaCosteo)}.{nameof(clsRutaCosteo.CostoFinal)}");
    }

    private static bool EnCamino(string espacio, string camino) =>
        clsCapas.Todas.Select(a => $"{a.GetName().Name}.{camino}")
            .Any(raiz => espacio == raiz || espacio.StartsWith(raiz + ".", StringComparison.Ordinal));

    private static IEnumerable<string> CamposConCosto(Type tipo, string ruta, HashSet<Type> vistos)
    {
        if (Elemento(tipo) is { } elemento)
        {
            tipo = elemento;
        }

        if (_heredadosSiempreNull.Contains(ruta) || tipo.Namespace?.StartsWith("Cotifly.", StringComparison.Ordinal) != true || !vistos.Add(tipo))
        {
            return [];
        }

        return tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
            .SelectMany(p => p.Name.StartsWith("Costo", StringComparison.Ordinal)
                ? [$"{ruta}.{p.Name}"]
                : CamposConCosto(p.PropertyType, $"{ruta}.{p.Name}", vistos))
            .ToList();
    }

    private static Type? Elemento(Type tipo)
    {
        if (Nullable.GetUnderlyingType(tipo) is { } subyacente)
        {
            return subyacente;
        }

        if (tipo.IsArray)
        {
            return tipo.GetElementType();
        }

        return tipo != typeof(string) && typeof(IEnumerable).IsAssignableFrom(tipo) && tipo.IsGenericType ? tipo.GetGenericArguments().Last() : null;
    }
}
