using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Comun.Exceptions;

/// <summary>
/// La base rechazó un cambio por una restricción única. Traduce el error técnico a un
/// <see cref="clsErrorDominio"/> que los casos de uso pueden devolver tal cual
/// (ej. <c>FOLIO_DUPLICADO</c> para <c>uq_cotizaciones_folio</c>).
/// </summary>
public sealed class clsViolacionUnicidadException : Exception
{
    public const string CodigoFolioDuplicado = "FOLIO_DUPLICADO";
    public const string CodigoRegistroDuplicado = "REGISTRO_DUPLICADO";

    public clsViolacionUnicidadException()
        : this(null, new clsErrorDominio(CodigoRegistroDuplicado, "El registro ya existe."), null)
    {
    }

    public clsViolacionUnicidadException(string message)
        : this(null, new clsErrorDominio(CodigoRegistroDuplicado, message), null)
    {
    }

    public clsViolacionUnicidadException(string message, Exception innerException)
        : this(null, new clsErrorDominio(CodigoRegistroDuplicado, message), innerException)
    {
    }

    public clsViolacionUnicidadException(string? restriccion, clsErrorDominio error, Exception? innerException)
        : base(error?.Mensaje, innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        Restriccion = restriccion;
        Error = error;
    }

    /// <summary>Nombre de la restricción o índice único que falló, si se pudo determinar.</summary>
    public string? Restriccion { get; }

    /// <summary>Error de negocio equivalente.</summary>
    public clsErrorDominio Error { get; }
}
