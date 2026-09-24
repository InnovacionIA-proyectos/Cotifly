using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Valida con <see cref="clsValidadorSecretos"/> todas las propiedades marcadas con
/// <see cref="clsSecretoAttribute"/> en una clase de opciones. Se registra automáticamente
/// para cada opción agregada con <see cref="clsRegistroOpciones.AddOpcionesValidadas{TOpciones}"/>.
/// </summary>
/// <typeparam name="TOpciones">Clase de opciones.</typeparam>
public sealed class clsValidadorSecretosOpciones<TOpciones> : IValidateOptions<TOpciones>
    where TOpciones : class
{
    private static readonly (PropertyInfo Propiedad, clsSecretoAttribute Secreto)[] _secretos =
        typeof(TOpciones).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (Propiedad: p, Secreto: p.GetCustomAttribute<clsSecretoAttribute>()))
            .Where(x => x.Secreto is not null)
            .Select(x => (x.Propiedad, x.Secreto!))
            .ToArray();

    private readonly IHostEnvironment _entorno;
    private readonly string _seccion;

    public clsValidadorSecretosOpciones(IHostEnvironment entorno, string seccion)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        ArgumentException.ThrowIfNullOrWhiteSpace(seccion);
        _entorno = entorno;
        _seccion = seccion;
    }

    public ValidateOptionsResult Validate(string? name, TOpciones options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var esProduccion = _entorno.IsProduction();
        var errores = _secretos
            .Select(s => clsValidadorSecretos.Validar(
                $"{_seccion}:{s.Propiedad.Name}",
                (string?)s.Propiedad.GetValue(options),
                s.Secreto.LargoMinimo,
                esProduccion))
            .OfType<string>()
            .ToList();

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }
}
