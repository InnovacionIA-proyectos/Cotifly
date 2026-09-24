namespace Cotifly.Dominio.Comun;

/// <summary>
/// Validaciones baratas y repetidas de las entidades. Devuelven el <see cref="clsErrorDominio"/>
/// a reportar o <c>null</c> si el dato es aceptable; así los <c>Crear</c> encadenan varias reglas
/// y devuelven la primera que falla.
/// </summary>
internal static class clsValidacion
{
    public static clsErrorDominio? Requerido(string codigo, string campo, string? valor, int? largoMaximo = null)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return new clsErrorDominio(codigo, $"El campo '{campo}' es obligatorio.");
        }

        return LargoMaximo(codigo, campo, valor, largoMaximo);
    }

    public static clsErrorDominio? LargoMaximo(string codigo, string campo, string? valor, int? largoMaximo)
    {
        if (valor is not null && largoMaximo is { } maximo && valor.Length > maximo)
        {
            return new clsErrorDominio(codigo, $"El campo '{campo}' supera {maximo} caracteres.");
        }

        return null;
    }

    public static clsErrorDominio? NoNegativo(string codigo, string campo, decimal? valor) =>
        valor is < 0m ? new clsErrorDominio(codigo, $"El campo '{campo}' no puede ser negativo.") : null;

    /// <summary>Normaliza texto opcional: <c>null</c> si está vacío, recortado si no.</summary>
    public static string? Normalizar(string? valor) =>
        string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    /// <summary>Primer error no nulo, o <c>null</c> si todas las reglas pasan.</summary>
    public static clsErrorDominio? Primero(params clsErrorDominio?[] errores) =>
        errores.FirstOrDefault(e => e is not null);
}
