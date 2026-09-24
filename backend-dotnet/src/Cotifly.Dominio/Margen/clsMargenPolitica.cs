using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Margen;

/// <summary>
/// Margen de política por línea de negocio (<c>dbo_cotifly.MargenesPolitica</c>). Las 8 líneas
/// vienen sembradas por el script 0001; <see cref="Id"/> es la FK <c>Cotizaciones.lineaNegocioId</c>.
/// </summary>
public sealed class clsMargenPolitica
{
    public const string CodigoError = "MARGEN_POLITICA_INVALIDO";
    public const string MetodoMargen = clsReglaMargen.MetodoMargen;
    public const int LargoMaximoLinea = 40;

    private clsMargenPolitica()
    {
    }

    public int Id { get; private set; }
    public string LineaNegocio { get; private set; } = string.Empty;
    public string? MapeoLineasNexo { get; private set; }
    public decimal Margen { get; private set; }
    public string MetodoCalculo { get; private set; } = MetodoMargen;
    public bool Activa { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Crea una línea con margen ≥ 0 (y &lt; 1 si el método es <c>margen</c>, ver <see cref="clsReglaMargen"/>).</summary>
    public static clsResultado<clsMargenPolitica> Crear(
        string lineaNegocio, decimal margen, string metodoCalculo = MetodoMargen, string? mapeoLineasNexo = null)
    {
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "lineaNegocio", lineaNegocio, LargoMaximoLinea),
            clsValidacion.Requerido(CodigoError, "metodoCalculo", metodoCalculo),
            clsReglaMargen.Validar(CodigoError, "margen", margen, metodoCalculo));

        if (error is not null)
        {
            return error;
        }

        return new clsMargenPolitica
        {
            LineaNegocio = lineaNegocio.Trim(),
            Margen = margen,
            MetodoCalculo = metodoCalculo.Trim(),
            MapeoLineasNexo = clsValidacion.Normalizar(mapeoLineasNexo),
            Activa = true,
        };
    }

    /// <summary>Cambia el margen de la línea (administración de líneas, HU-010).</summary>
    public clsResultado<clsMargenPolitica> CambiarMargen(decimal margen)
    {
        if (clsReglaMargen.Validar(CodigoError, "margen", margen, MetodoCalculo) is { } error)
        {
            return error;
        }

        Margen = margen;
        return this;
    }

    public void Activar() => Activa = true;

    public void Desactivar() => Activa = false;
}
