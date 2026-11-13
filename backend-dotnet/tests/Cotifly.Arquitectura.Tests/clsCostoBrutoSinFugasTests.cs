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
/// El costo bruto de la ruta (<see cref="clsRutaCosteo.CostoFinal"/>) solo sale por la vista del costeo del dueño
/// (<c>GET .../costeo</c>, las acciones que la devuelven y el detalle). Reversa acotada de ADR-015 §5: ni el chat o asistente, ni el PDF, ni
/// el correo, ni el Excel de la licitación, ni las métricas, ni "Mis cotizaciones" o el listado regional usan esa vista, y fuera de las
/// métricas ninguno trae un costo propio en su respuesta. Las métricas sí traen sus agregados de costeo (<c>costeo_total</c>,
/// <c>costeo_ganadas</c>, <c>ganancias</c>): son sumas de periodo, no el costo de una ruta, y no empiezan por
/// <c>Costo</c>, por eso pasan <see cref="LasRespuestasDeLosDemasCaminosNoTienenCampoDeCosto"/>.
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

    /// <summary>
    /// En todo el árbol del detalle por folio (cabecera, costeo, captura y totales) el único campo de costo es el costo
    /// bruto de las rutas del costeo, el que <c>SinCostoFinal()</c> pone en <c>null</c> para el gerente.
    /// </summary>
    [Fact]
    public void ElDetalleSoloTraeElCostoBrutoEnLasRutasDeSuCosteo()
    {
        // La ruta del costeo trae DOS campos de costo: el costo bruto y el costo del transporte (fletes + retorno +
        // levante, sin conexos ni margen). Siguen sin aparecer el desglose (fletes, retorno, levante) ni el costo de cada camión o conexo.
        var raiz = $"{nameof(clsDetalleCotizacion)}.{nameof(clsDetalleCotizacion.Costeo)}.{nameof(clsCosteoCotizacion.Rutas)}";
        CamposConCosto(typeof(clsDetalleCotizacion), nameof(clsDetalleCotizacion), []).Should()
            .Equal($"{raiz}.{nameof(clsRutaCosteo.CostoFinal)}", $"{raiz}.{nameof(clsRutaCosteo.CostoTransporte)}");
    }

    [Fact]
    public void LaRutaDelCosteoSoloTraeElCostoBrutoYElCostoDelTransporteSinDesglose()
    {
        // Dos campos de costo (CostoFinal y CostoTransporte); nunca fletes, retorno, levante ni el costo de cada camión o conexo.
        CamposConCosto(typeof(clsRutaCosteo), nameof(clsRutaCosteo), []).Should()
            .Equal($"{nameof(clsRutaCosteo)}.{nameof(clsRutaCosteo.CostoFinal)}", $"{nameof(clsRutaCosteo)}.{nameof(clsRutaCosteo.CostoTransporte)}");
    }

    /// <summary>
    /// En el Excel de la cotización (<c>GET /web/cotizaciones/{folio}/excel</c>) todo campo de costo
    /// (<c>Costo*</c>) o de margen (<c>Margen*</c>: con el margen y el precio se deduce el costo) cuelga de una propiedad <c>Interno</c>, que
    /// es lo que <see cref="clsExcelCotizacion.SinCostos"/> quita para el gerente y el director. Un campo de costo nuevo fuera de
    /// <c>Interno</c> rompe esta prueba antes de llegar al Excel del lector que no es dueño.
    /// </summary>
    [Fact]
    public void ElExcelDeLaCotizacionSoloTraeCostosYMargenesDentroDeInterno()
    {
        var internos = CamposInternos(typeof(clsExcelCotizacion), nameof(clsExcelCotizacion), []).ToList();

        internos.Should().NotBeEmpty("la versión del dueño sí trae costos y márgenes");
        internos.Should().Contain($"{nameof(clsExcelCotizacion)}.{nameof(clsExcelCotizacion.Rutas)}.{nameof(clsExcelRuta.Interno)}.{nameof(clsExcelInternoRuta.CostoBruto)}");
        internos.Where(c => !c.Split('.').Contains(nameof(clsExcelRuta.Interno))).Should()
            .BeEmpty("el costo y el margen del Excel solo viajan dentro de Interno, que SinCostos() quita");
    }

    /// <summary>
    /// El Excel de la cotización no usa la vista del costeo (<see cref="clsCosteoCotizacion"/>): arma su propio
    /// modelo desde lo guardado, con lo interno aparte.
    /// </summary>
    [Fact]
    public void ElExcelDeLaCotizacionNoUsaLaVistaDelCosteo()
    {
        var prohibidos = _vistasConCosto.Select(t => t.FullName!).ToHashSet(StringComparer.Ordinal);
        string[] excel =
        [
            typeof(Cotifly.Aplicacion.Cotizaciones.Services.clsExportacionCotizacionService).FullName!,
            typeof(Cotifly.Infraestructura.Cotizaciones.Services.clsExportadorExcelCotizacionClosedXml).FullName!,
        ];

        var tipos = clsCapas.Modelo.Types.Where(t => excel.Any(e => t.FullName == e || t.FullName.StartsWith(e + "+", StringComparison.Ordinal))).ToList();

        tipos.Select(t => t.FullName).Should().Contain(excel);
        tipos.SelectMany(t => t.Dependencies).Where(d => prohibidos.Contains(d.Target.FullName)).Should().BeEmpty();
    }

    /// <summary>
    /// <see cref="clsExcelCotizacion.SinCostos"/> deja en <c>null</c> TODAS las propiedades <c>Interno</c> del
    /// árbol (cabecera, rutas, camiones, conexos y totales), con todas llenas en la versión del dueño.
    /// </summary>
    [Fact]
    public void SinCostosDejaElExcelDelLectorSinNadaInterno()
    {
        var interno = new clsExcelCotizacion(
            "PCN-AB-01012026-001", "Cliente", "900111222", "Ana", "spot", "COP", 0m, "entregada", null, null, false, DateTimeOffset.UnixEpoch, false,
            [
                new clsExcelRuta(1, "Buenaventura", "Bogotá", "Costeada", false, null, 1000m, 1, 100m, 10m, 110m,
                    [new clsExcelCamion(1, "3S3", "FURGON", 1000m, "Costeado", true, 100m, new clsExcelInternoCamion(80m, 0m, 0m, 80m, 0.2m))],
                    [new clsExcelConexo("Cargue", "logistico", 1m, 10m, new clsExcelInternoConexo(8m))],
                    new clsExcelInternoRuta(80m, 0m, 0m, 8m, 88m, 0.2m, 0.2m, "politica_linea", "margen")),
            ],
            new clsExcelTotales(1, 1, 0, 1000m, 100m, 10m, 110m, new clsExcelInternoTotales(88m)));

        ValoresInternos(interno).Should().HaveCount(4, "la versión del dueño trae lo interno de la ruta, el camión, el conexo y los totales");
        var lector = interno.SinCostos();

        lector.SoloLectura.Should().BeTrue();
        ValoresInternos(lector).Should().BeEmpty("el Excel del gerente y del director no lleva nada interno");
        lector.Rutas[0].PrecioRuta.Should().Be(110m, "los precios se conservan");
    }

    /// <summary>Las propiedades <c>Interno</c> con valor en todo el árbol de <paramref name="valor"/>.</summary>
    private static List<object> ValoresInternos(object? valor)
    {
        if (valor is null || valor is string || valor.GetType().Namespace?.StartsWith("Cotifly.", StringComparison.Ordinal) != true)
        {
            return valor is IEnumerable lista and not string ? [.. lista.Cast<object?>().SelectMany(ValoresInternos)] : [];
        }

        var encontrados = new List<object>();
        foreach (var propiedad in valor.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract"))
        {
            var hijo = propiedad.GetValue(valor);
            if (propiedad.Name == nameof(clsExcelRuta.Interno) && hijo is not null)
            {
                encontrados.Add(hijo);
            }

            encontrados.AddRange(ValoresInternos(hijo));
        }

        return encontrados;
    }

    /// <summary>Rutas de los campos <c>Costo*</c> o <c>Margen*</c> en todo el árbol de <paramref name="tipo"/>.</summary>
    private static IEnumerable<string> CamposInternos(Type tipo, string ruta, HashSet<Type> vistos)
    {
        if (Elemento(tipo) is { } elemento)
        {
            tipo = elemento;
        }

        if (tipo.Namespace?.StartsWith("Cotifly.", StringComparison.Ordinal) != true || !vistos.Add(tipo))
        {
            return [];
        }

        return tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
            .SelectMany(p => p.Name.StartsWith("Costo", StringComparison.Ordinal) || p.Name.StartsWith("Margen", StringComparison.Ordinal)
                ? [$"{ruta}.{p.Name}"]
                : CamposInternos(p.PropertyType, $"{ruta}.{p.Name}", vistos))
            .ToList();
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
