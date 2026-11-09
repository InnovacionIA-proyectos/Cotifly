using System.Collections;
using System.Reflection;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Pdf.DTOs;

namespace Cotifly.Arquitectura.Tests;

/// <summary>
/// HU-045.3 (#265, ADR-029 §9): el seguro de la mercancía sale en el PDF, el Excel (cotización y licitación), el correo y los totales del
/// detalle como <c>valor_seguro</c> / <c>precio_seguro</c>, nunca con la tasa (<c>Tasa*</c>) ni como costo (<c>CostoSeguro</c>). Complementa
/// <c>clsCostoBrutoSinFugasTests</c>, que no se relaja: ningún campo <c>Costo*</c> nuevo.
/// </summary>
public sealed class clsSeguroSinTasaEnExportacionesTests
{
    [Theory]
    [InlineData(typeof(clsExcelCotizacion))]
    [InlineData(typeof(clsExportacionLicitacion))]
    [InlineData(typeof(clsResumenCorreo))]
    [InlineData(typeof(clsVistaPreviaCorreo))]
    [InlineData(typeof(clsDocumentoCotizacion))]
    [InlineData(typeof(clsTotalesCotizacion))]
    public void LasExportacionesNoTraenLaTasaNiElCostoDelSeguro(Type exportacion)
    {
        Campos(exportacion, exportacion.Name, [])
            .Where(c => c.Split('.')[^1] is var nombre && (nombre.StartsWith("Tasa", StringComparison.Ordinal) || nombre == "CostoSeguro"))
            .Should().BeEmpty($"{exportacion.Name} muestra el valor del seguro, nunca la tasa ni un costo");
    }

    [Fact]
    public void LaReglaNoEsVaciaElSeguroSaleComoValorEnElExcelYEnLosTotales()
    {
        Campos(typeof(clsExcelCotizacion), nameof(clsExcelCotizacion), []).Should()
            .Contain($"{nameof(clsExcelCotizacion)}.{nameof(clsExcelCotizacion.Rutas)}.{nameof(clsExcelRuta.ValorSeguro)}")
            .And.Contain($"{nameof(clsExcelCotizacion)}.{nameof(clsExcelCotizacion.Totales)}.{nameof(clsExcelTotales.PrecioSeguro)}");
        Campos(typeof(clsTotalesCotizacion), nameof(clsTotalesCotizacion), []).Should()
            .Contain($"{nameof(clsTotalesCotizacion)}.{nameof(clsTotalesCotizacion.PrecioSeguro)}");
    }

    /// <summary>Rutas de todos los campos públicos del árbol de <paramref name="tipo"/> (los tipos de Cotifly se recorren una vez).</summary>
    private static List<string> Campos(Type tipo, string ruta, HashSet<Type> vistos)
    {
        if (Elemento(tipo) is { } elemento)
        {
            tipo = elemento;
        }

        if (tipo.Namespace?.StartsWith("Cotifly.", StringComparison.Ordinal) != true || !vistos.Add(tipo))
        {
            return [];
        }

        return [.. tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
            .SelectMany(p => (IEnumerable<string>)[$"{ruta}.{p.Name}", .. Campos(p.PropertyType, $"{ruta}.{p.Name}", vistos)])];
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
