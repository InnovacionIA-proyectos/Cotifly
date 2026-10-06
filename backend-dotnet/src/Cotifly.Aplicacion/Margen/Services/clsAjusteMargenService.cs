using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;
using Cotifly.Dominio.Precio;

namespace Cotifly.Aplicacion.Margen.Services;

/// <summary>
/// Implementación de <see cref="IClsAjusteMargenService"/> (port de <c>registrar_ajuste_margen</c> + margen
/// negociado de HU-003.4):
/// <list type="bullet">
/// <item>Los márgenes se redondean a 4 decimales (escala de <c>MargenAjustes</c>) y se validan por método con
/// <see cref="clsReglaMargen"/> (<c>markup</c> admite m ≥ 1); fuera de rango o método desconocido → <c>AJUSTE_MARGEN_INVALIDO</c>.</item>
/// <item>El precio usa el valor manual guardado: no se vuelve a resolver la política (costo inválido → <c>PRECIO_INVALIDO</c>).</item>
/// <item>El ajuste queda en <c>MargenAjustes</c> con el comercial (<c>usuarioId</c>) y
/// diferencia = aplicado − política; la cotización, si viene, debe ser del comercial.</item>
/// </list>
/// Nada se guarda si alguna validación falla.
/// </summary>
public sealed class clsAjusteMargenService(
    IClsMargenAjusteRepository ajustes,
    IClsCotizacionRepository cotizaciones,
    IClsUnitOfWork unidadTrabajo) : IClsAjusteMargenService
{
    /// <summary>Motivo que se registra cuando el comercial no escribe uno (la columna lo exige en el dominio).</summary>
    public const string MotivoPorDefecto = "Ajuste manual del comercial";

    public async Task<clsResultado<clsSalidaAjusteMargen>> RegistrarAsync(clsEntradaAjusteMargen entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        if (entrada.UsuarioId == Guid.Empty)
        {
            throw new ArgumentException("El ajuste de margen exige el comercial (usuarioId).", nameof(entrada));
        }

        var metodo = string.IsNullOrWhiteSpace(entrada.MetodoCalculo) ? clsReglaMargen.MetodoMargen : entrada.MetodoCalculo.Trim().ToLowerInvariant();
        if (metodo is not (clsReglaMargen.MetodoMargen or clsReglaMargen.MetodoMarkup))
        {
            return new clsErrorDominio(clsCodigosError.AjusteMargenInvalido, $"Método desconocido: '{entrada.MetodoCalculo}'. Use 'margen' o 'markup'.");
        }

        // Los márgenes se llevan a la escala de la columna (4 decimales) antes de validar, calcular y guardar:
        // la salida refleja exactamente lo que queda en MargenAjustes.
        var politica = clsMargenAjuste.RedondearMargen(entrada.MargenPolitica);
        var aplicado = clsMargenAjuste.RedondearMargen(entrada.MargenAplicado);
        var errorRango = clsReglaMargen.Validar(clsCodigosError.AjusteMargenInvalido, "margenAplicado", aplicado, metodo)
                         ?? clsReglaMargen.Validar(clsCodigosError.AjusteMargenInvalido, "margenPolitica", politica, metodo);
        if (errorRango is not null)
        {
            return errorRango;
        }

        clsSalidaPrecio? precio = null;
        if (entrada.Costo is { } costo)
        {
            var calculado = clsCalculadoraPrecio.Calcular(costo, aplicado, metodo);
            if (calculado.EsFallo)
            {
                return calculado.Error;
            }

            precio = clsSalidaPrecio.Desde(calculado.Valor);
        }

        var folio = entrada.Folio;
        if (entrada.CotizacionId is { } cotizacionId)
        {
            var cotizacion = await cotizaciones.ObtenerPorIdDelUsuarioAsync(cotizacionId, entrada.UsuarioId, cancellationToken);
            if (cotizacion is null)
            {
                return new clsErrorDominio(clsCodigosError.AjusteCotizacionInvalida, $"No existe la cotización {cotizacionId}.");
            }

            folio ??= cotizacion.Folio;
        }

        var motivo = string.IsNullOrWhiteSpace(entrada.Motivo) ? MotivoPorDefecto : entrada.Motivo.Trim();
        var registro = clsMargenAjuste.Registrar(politica, aplicado, motivo, entrada.UsuarioId, entrada.CotizacionId, folio, metodo);
        if (registro.EsFallo)
        {
            return registro.Error;
        }

        var ajuste = registro.Valor;
        ajustes.Agregar(ajuste);
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);

        var aplicadoGuardado = clsReglasMargen.Redondear(ajuste.MargenAplicado ?? aplicado);
        var politicaGuardada = clsReglasMargen.Redondear(ajuste.MargenPolitica ?? politica);
        var diferencia = clsReglasMargen.Redondear(ajuste.Diferencia ?? 0m);
        var detalle = $"Margen negociado por el comercial: {clsReglasMargen.Texto(aplicadoGuardado)} "
                      + $"(política {clsReglasMargen.Texto(politicaGuardada)}, diferencia {clsReglasMargen.Texto(diferencia)}). Motivo: {motivo}.";

        return new clsSalidaAjusteMargen(
            new clsSalidaMargen(aplicadoGuardado, clsCodigoEnum.ACodigo(OrigenMargen.Negociado), entrada.TrqType, clsReglasMargen.EsAnomalo(aplicadoGuardado), detalle),
            Registrado: true,
            diferencia,
            ajuste.Id,
            precio);
    }
}
