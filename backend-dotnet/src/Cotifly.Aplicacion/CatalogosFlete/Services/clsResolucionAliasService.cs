using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Comun;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.CatalogosFlete.Services;

/// <summary>Un texto de <c>GET /web/alias/resolver</c> y lo que se sabe de él.</summary>
/// <param name="Texto">El texto tal como llegó (recortado).</param>
/// <param name="Reconocido"><c>false</c> si ni el catálogo oficial ni el diccionario de alias de Nexo lo conocen.</param>
/// <param name="Codigo">El código oficial de Nexo (<c>2L2</c>, <c>CNT40.HC</c>), o <c>null</c> si no se reconoce.</param>
/// <param name="Fuente"><c>catalogo</c> (ya es un código oficial), <c>alias</c> (lo tradujo el diccionario) o <c>null</c>.</param>
public sealed record clsAliasResuelto(string Texto, bool Reconocido, string? Codigo, string? Fuente);

/// <summary>Respuesta de <c>GET /web/alias/resolver</c>: el tipo pedido y un resultado por texto, en el mismo orden.</summary>
/// <param name="Tipo"><c>vehiculo</c> o <c>contenedor</c>.</param>
/// <param name="Resultados">Un resultado por cada <c>texto</c> pedido, en el mismo orden (repetidos incluidos).</param>
public sealed record clsAliasResueltos(string Tipo, IReadOnlyList<clsAliasResuelto> Resultados);

/// <summary>
/// <c>GET /web/alias/resolver</c>. El front de Cotifly no puede llamar a Nexo (CORS e identidad por header), así que el
/// backend resuelve por él un tipo de vehículo o una clase de contenedor escritos a mano (por ejemplo, al importar el Excel de una
/// licitación), con la misma regla que la captura y el envío (<see cref="clsResolucionCodigosNexo"/>: catálogo oficial y, si no, el
/// diccionario de alias de Nexo).
/// </summary>
public interface IClsResolucionAliasService
{
    /// <summary>
    /// Resuelve de 1 a <see cref="clsResolucionAliasService.MaximoTextos"/> textos de <paramref name="tipo"/> (<c>vehiculo</c> |
    /// <c>contenedor</c>) en una sola llamada (en lote: una licitación grande no hace una petición por fila). <c>SOLICITUD_INVALIDA</c>
    /// si el tipo no es válido, no hay textos, hay demasiados o uno es muy largo; <c>CATALOGOS_FLETE_NO_DISPONIBLE</c> si hace falta el
    /// catálogo o el diccionario de Nexo y no hay una copia utilizable.
    /// </summary>
    Task<clsResultado<clsAliasResueltos>> ResolverAsync(Guid usuarioId, string? tipo, IReadOnlyList<string?>? textos, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IClsResolucionAliasService"/>
public sealed class clsResolucionAliasService(IClsCatalogosMaestrosProvider maestros, IClsAliasNexoProvider alias) : IClsResolucionAliasService
{
    /// <summary>Máximo de textos por llamada.</summary>
    public const int MaximoTextos = 200;

    /// <summary>Largo máximo de cada texto (el de un alias en Nexo).</summary>
    public const int LargoMaximoTexto = 100;

    public const string FuenteCatalogo = "catalogo";
    public const string FuenteAlias = "alias";

    public async Task<clsResultado<clsAliasResueltos>> ResolverAsync(
        Guid usuarioId, string? tipo, IReadOnlyList<string?>? textos, CancellationToken cancellationToken = default)
    {
        if (!clsDiccionarioAliasNexo.TryParseTipo(tipo, out var tipoAlias))
        {
            return Invalida("El parámetro 'tipo' debe ser 'vehiculo' o 'contenedor'.");
        }

        var limpios = (textos ?? []).Select(t => t?.Trim()).ToList();
        if (limpios.Count == 0 || limpios.Any(string.IsNullOrEmpty))
        {
            return Invalida("Indica al menos un 'texto' a resolver, sin valores vacíos.");
        }

        if (limpios.Count > MaximoTextos)
        {
            return Invalida($"Se pueden resolver máximo {MaximoTextos} textos por llamada.");
        }

        if (limpios.Any(t => t!.Length > LargoMaximoTexto))
        {
            return Invalida($"Cada 'texto' admite máximo {LargoMaximoTexto} caracteres.");
        }

        var catalogos = await maestros.ObtenerAsync(usuarioId, cancellationToken);
        if (catalogos.EsFallo)
        {
            return catalogos.Error;
        }

        var tipos = catalogos.Valor.TiposVehiculo.Select(o => o.Valor).ToList();
        var clases = catalogos.Valor.ClasesContenedor.Select(o => o.Valor).ToList();
        if (tipoAlias == TipoAliasNexo.Contenedor && clases.Count == 0)
        {
            // Sin el catálogo de clases de contenedor no se puede decir qué es oficial (la captura tampoco valida sin él).
            return new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, clsMensajes.SinClasesContenedor);
        }

        clsCodigoResueltoNexo? Resolver(string texto, clsDiccionarioAliasNexo? diccionario) => tipoAlias == TipoAliasNexo.Vehiculo
            ? clsResolucionCodigosNexo.Vehiculo(tipos, diccionario, texto)
            : clsResolucionCodigosNexo.ClaseContenedor(clases, diccionario, texto);

        clsDiccionarioAliasNexo? diccionario = null;
        if (limpios.Any(t => Resolver(t!, null) is null))
        {
            var obtenido = await alias.ObtenerAsync(usuarioId, cancellationToken);
            if (obtenido.EsFallo)
            {
                return obtenido.Error;
            }

            diccionario = obtenido.Valor;
        }

        var resultados = limpios.Select(t =>
        {
            var resuelto = Resolver(t!, diccionario);
            return new clsAliasResuelto(
                t!, resuelto is not null, resuelto?.Codigo,
                resuelto is null ? null : resuelto.Fuente == FuenteCodigoNexo.Catalogo ? FuenteCatalogo : FuenteAlias);
        }).ToList();
        return new clsAliasResueltos(tipoAlias == TipoAliasNexo.Vehiculo ? "vehiculo" : "contenedor", resultados);
    }

    private static clsErrorDominio Invalida(string mensaje) => new(clsCodigosAplicacion.SolicitudInvalida, mensaje);

    private static class clsMensajes
    {
        public const string SinClasesContenedor =
            "El catálogo de clases de contenedor de Nexo no está disponible en este momento: intenta de nuevo más tarde.";
    }
}
