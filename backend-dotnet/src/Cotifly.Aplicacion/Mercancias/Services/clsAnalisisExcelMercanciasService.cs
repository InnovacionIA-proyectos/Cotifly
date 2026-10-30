using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Cubicacion.Services;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Mercancias.DTOs;
using Cotifly.Aplicacion.Mercancias.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Mercancias.Services;

/// <summary>
/// Análisis del Excel de mercancías de una ruta (HU-035.1, #202, ADR-024): valida el archivo con las reglas genéricas
/// (<see cref="clsArchivoExcel"/>), lo lee localmente como grilla cruda (<see cref="IClsLectorGrillaExcel"/>) y, si alguna hoja tiene
/// columna de mercancía o de código, sugiere el código de cada fila contra el catálogo del cubicador. <b>Nunca llama a Nexo</b> y no
/// guarda nada. El catálogo es opcional: sin cubicador o con él caído, el análisis sale igual, sin sugerencias.
/// </summary>
public interface IClsAnalisisExcelMercanciasService
{
    Task<clsResultado<clsAnalisisExcelMercancias>> AnalizarAsync(Guid usuarioId, clsArchivoSubido archivo, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsAnalisisExcelMercanciasService"/>.</summary>
public sealed class clsAnalisisExcelMercanciasService(
    IClsLectorGrillaExcel lector,
    ILogger<clsAnalisisExcelMercanciasService> logger,
    IClsCubicador? cubicador = null) : IClsAnalisisExcelMercanciasService
{
    public async Task<clsResultado<clsAnalisisExcelMercancias>> AnalizarAsync(
        Guid usuarioId, clsArchivoSubido archivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archivo);
        if (usuarioId == Guid.Empty)
        {
            return new clsErrorDominio(clsCodigosError.ErrorInterno, "El análisis exige el comercial autenticado.");
        }

        if (clsArchivoExcel.ValidarArchivo(archivo.Nombre, archivo.Contenido) is { } invalido)
        {
            return invalido;
        }

        var leido = await lector.LeerAsync(clsArchivoExcel.NombreLimpio(archivo.Nombre), archivo.Contenido, cancellationToken);
        return leido.EsFallo ? leido : await ConMercanciasAsync(leido.Valor, cancellationToken);
    }

    /// <summary>
    /// Igual que el análisis de licitaciones (HU-023.5): por hoja, la columna de la mercancía y la de su código y, por fila, el código
    /// tal cual o el más parecido del catálogo (solo tipos cubicables), marcado como sugerido. El catálogo se lee una vez y solo si alguna
    /// hoja tiene una de esas columnas.
    /// </summary>
    private async Task<clsAnalisisExcelMercancias> ConMercanciasAsync(clsAnalisisExcelMercancias analisis, CancellationToken cancellationToken)
    {
        var columnas = analisis.Hojas
            .Select(h => (Hoja: h, Texto: clsDetectorMercanciaExcel.ColumnaMercancia(h.Encabezados), Codigo: clsDetectorMercanciaExcel.ColumnaCodigoMercancia(h.Encabezados)))
            .ToList();
        if (columnas.All(c => c.Texto is null && c.Codigo is null))
        {
            return analisis;
        }

        var catalogo = await clsDetectorMercanciaExcel.CatalogoAsync(cubicador, logger, cancellationToken);
        var porCodigo = clsDetectorMercanciaExcel.PorCodigo(catalogo);
        return analisis with
        {
            CatalogoMercanciasDisponible = catalogo is not null,
            Hojas = [.. columnas.Select(c => c.Texto is null && c.Codigo is null
                ? c.Hoja
                : c.Hoja with
                {
                    ColumnaMercancia = c.Texto is { } t ? c.Hoja.Encabezados[t] : null,
                    ColumnaCodigoMercancia = c.Codigo is { } k ? c.Hoja.Encabezados[k] : null,
                    Mercancias = clsDetectorMercanciaExcel.Mercancias(c.Hoja.Filas, c.Texto, c.Codigo, catalogo, porCodigo),
                })],
        };
    }
}
