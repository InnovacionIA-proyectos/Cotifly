using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharpYaml.Serialization;

namespace Cotifly.Api.Tests.Configuracion;

/// <summary>
/// Endurecimiento A (#199): los manifiestos de Dokploy (<c>infra/stack.dotnet.dokploy.yml</c> y <c>infra/stack.dotnet.staging.yml</c>) no
/// usan <c>env_file</c>, así que una variable que no declaran NO llega al contenedor aunque esté cargada en Dokploy (así quedaron fuera
/// <c>Cubicador__*</c>, <c>Correo__Seguimiento__*</c>...). Estas pruebas leen el YAML y lo comparan con las clases de opciones que la API
/// enlaza a la configuración (todas con <c>ValidateOnStart</c>):
/// <list type="bullet">
/// <item>cada opción enlazada (incluidas las anidadas y el primer elemento de las listas) está declarada en los dos manifiestos, salvo
/// las de <see cref="NoDeclaradas"/>, con su motivo;</item>
/// <item>ningún manifiesto declara una variable que el código no lee (un nombre mal escrito o una opción retirada);</item>
/// <item>el valor por defecto (<c>${VAR:-x}</c>) o el valor fijo de cada variable es el del código, salvo las diferencias a propósito de
/// <see cref="DiferenciasAProposito"/>.</item>
/// </list>
/// </summary>
public sealed partial class clsManifiestosDespliegueTests
{
    public static readonly string[] Manifiestos = ["stack.dotnet.dokploy.yml", "stack.dotnet.staging.yml"];

    /// <summary>Opciones enlazadas que los manifiestos no declaran a propósito, con el motivo.</summary>
    public static readonly IReadOnlyDictionary<string, string> NoDeclaradas = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ForwardedHeaders__ProxiesConocidos__0"] =
            "Con ${VAR:-} vacía la API no arranca (no es una IP); los manifiestos usan ForwardedHeaders__RedesConocidas__0 (la subred de dokploy-network).",
    };

    /// <summary>Variables que se declaran sin ser una opción enlazada de Cotifly.</summary>
    public static readonly IReadOnlyDictionary<string, string> AjenasALasOpciones = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Entorno de ASP.NET Core: Production fijo en los dos stacks.",
    };

    /// <summary>(manifiesto o *, variable) cuyo valor difiere del valor por defecto del código a propósito.</summary>
    public static readonly IReadOnlyDictionary<(string Manifiesto, string Variable), string> DiferenciasAProposito =
        new Dictionary<(string, string), string>
        {
            [("*", "ForwardedHeaders__Habilitado")] = "true fijo: la API corre detrás de Traefik.",
            [("*", "Nexo__EnviarCodigosCubicador")] = "siempre explícito (HU-014 #188, HU-031.1 #194): en el código, vacío ya significa siempre.",
            [("stack.dotnet.staging.yml", "CosteoNexo__Habilitado")] =
                "Apagado en staging hasta que el DBA confirme que staging y producción no comparten dbo_cotifly (DEPLOY.md §2.2).",
            [("*", "Pdf__TextoSeguros")] =
                "#261 (HU-046): vacío en los stacks (Jurídica carga el texto oficial en Dokploy); el marcador provisional del código es solo para dev/QA y no debe salir a un cliente.",
        };

    [Fact]
    public void LasOpcionesEnlazadasSonExactamenteLasClasesConSeccion()
    {
        var enlazadas = TiposEnlazados();
        var conSeccion = TiposConSeccion().Select(t => t.Tipo).ToHashSet();

        enlazadas.Should().NotBeEmpty();
        enlazadas.Should().BeEquivalentTo(conSeccion,
            "cada clase de opciones enlazada a la configuración declara su sección (const Seccion) y toda clase con Seccion se enlaza");
    }

    public static TheoryData<string> CadaManifiesto()
    {
        var datos = new TheoryData<string>();
        foreach (var manifiesto in Manifiestos)
        {
            datos.Add(manifiesto);
        }

        return datos;
    }

    [Theory]
    [MemberData(nameof(CadaManifiesto))]
    public void CadaOpcionValidadaAlArrancarEstaDeclaradaEnElManifiesto(string manifiesto)
    {
        var declaradas = Variables(manifiesto).Keys.Select(Normalizar).ToHashSet(StringComparer.Ordinal);

        var faltantes = Esperadas().Keys.Where(v => !declaradas.Contains(v) && !NoDeclaradas.ContainsKey(v)).Order(StringComparer.Ordinal).ToList();

        faltantes.Should().BeEmpty($"{manifiesto} no usa env_file: una variable que no declara no llega al contenedor");
        NoDeclaradas.Keys.Should().OnlyContain(v => Esperadas().ContainsKey(v), "una excepción que ya no es una opción sobra");
    }

    [Theory]
    [MemberData(nameof(CadaManifiesto))]
    public void ElManifiestoNoDeclaraVariablesQueElCodigoNoLee(string manifiesto)
    {
        var esperadas = Esperadas();

        var sobrantes = Variables(manifiesto).Keys
            .Where(v => !esperadas.ContainsKey(Normalizar(v)) && !AjenasALasOpciones.ContainsKey(v))
            .Order(StringComparer.Ordinal)
            .ToList();

        sobrantes.Should().BeEmpty("cada variable del manifiesto es una opción que el código lee (nombre exacto, con __)");
    }

    [Fact]
    public void LosDosManifiestosDeclaranLasMismasVariables()
    {
        var produccion = Variables(Manifiestos[0]).Keys.ToHashSet(StringComparer.Ordinal);
        var staging = Variables(Manifiestos[1]).Keys.ToHashSet(StringComparer.Ordinal);

        staging.Should().BeEquivalentTo(produccion, "staging es producción con otros nombres y valores, no con otras variables");
    }

    [Theory]
    [MemberData(nameof(CadaManifiesto))]
    public void ElValorPorDefectoDeCadaVariableEsElDelCodigo(string manifiesto)
    {
        var esperadas = Esperadas();
        var errores = new List<string>();
        foreach (var (variable, valor) in Variables(manifiesto))
        {
            if (!esperadas.TryGetValue(Normalizar(variable), out var delCodigo) || delCodigo.EsLista
                || DiferenciasAProposito.ContainsKey(("*", variable)) || DiferenciasAProposito.ContainsKey((manifiesto, variable)))
            {
                continue;
            }

            var porDefecto = ValorPorDefecto(valor);
            if (porDefecto is null)
            {
                continue; // ${VAR:?...}: obligatoria, sin valor por defecto.
            }

            if (!string.Equals(porDefecto, delCodigo.Valor, StringComparison.Ordinal))
            {
                errores.Add($"{variable}: el manifiesto dice '{porDefecto}' y el código '{delCodigo.Valor}'");
            }
        }

        errores.Should().BeEmpty($"los valores por defecto de {manifiesto} son los del código");
    }

    [Fact]
    public void LosSecretosNoTienenValorPorDefecto()
    {
        var secretos = new[] { "Cubicador__ApiKey", "IA__ApiKey", "Jwt__SigningKey", "ConnectionStrings__Cotifly", "Cubicador__UrlBase", "SapTm__Pass", "ConnectionStrings__DwOperaciones" };
        foreach (var manifiesto in Manifiestos)
        {
            var variables = Variables(manifiesto);
            foreach (var secreto in secretos)
            {
                variables.Should().ContainKey(secreto);
                ValorPorDefecto(variables[secreto]).Should().BeNullOrEmpty($"{secreto} en {manifiesto} va sin valor real (solo en Dokploy)");
            }
        }
    }

    /// <summary>Variables del servicio <c>cotifly-api</c> del manifiesto (nombre y valor tal como está escrito).</summary>
    public static IReadOnlyDictionary<string, string> Variables(string manifiesto)
    {
        var ruta = Path.Combine(AppContext.BaseDirectory, "infra", manifiesto);
        var yaml = new YamlStream();
        using (var lector = new StringReader(File.ReadAllText(ruta)))
        {
            yaml.Load(lector);
        }

        var servicio = Hijo(Hijo((YamlMappingNode)yaml.Documents[0].RootNode, "services"), "cotifly-api");
        return Hijo(servicio, "environment").Children
            .ToDictionary(p => ((YamlScalarNode)p.Key).Value!, p => ((YamlScalarNode)p.Value).Value ?? string.Empty, StringComparer.Ordinal);
    }

    /// <summary>
    /// Variables que el código lee (con <c>__</c>), con el valor por defecto del código como texto (vacío si es <c>null</c>). Las listas se
    /// representan por su primer elemento (<c>__0</c>) y no tienen valor por defecto comparable.
    /// </summary>
    public static IReadOnlyDictionary<string, (string Valor, bool EsLista)> Esperadas()
    {
        var esperadas = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        foreach (var (tipo, seccion) in TiposConSeccion())
        {
            Expandir(tipo, Activator.CreateInstance(tipo), seccion, esLista: false, esperadas);
        }

        return esperadas;
    }

    /// <summary>Índices de lista normalizados a 0 (<c>IA__Fallbacks__1__Modelo</c> → <c>IA__Fallbacks__0__Modelo</c>).</summary>
    private static string Normalizar(string variable) => IndiceLista().Replace(variable, "__0${fin}");

    /// <summary>El valor por defecto de <c>${VAR:-x}</c> (x), el valor fijo si no es una referencia, o <c>null</c> en <c>${VAR:?...}</c>.</summary>
    private static string? ValorPorDefecto(string valor)
    {
        var referencia = Referencia().Match(valor);
        if (!referencia.Success)
        {
            return valor;
        }

        return referencia.Groups["op"].Value == ":-" ? referencia.Groups["defecto"].Value : null;
    }

    private static void Expandir(Type tipo, object? instancia, string prefijo, bool esLista, Dictionary<string, (string, bool)> esperadas)
    {
        foreach (var propiedad in tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetSetMethod() is not null && p.GetIndexParameters().Length == 0))
        {
            var nombre = $"{prefijo}__{propiedad.Name}";
            var valor = instancia is null ? null : propiedad.GetValue(instancia);
            var tipoPropiedad = Nullable.GetUnderlyingType(propiedad.PropertyType) ?? propiedad.PropertyType;
            if (EsSimple(tipoPropiedad))
            {
                esperadas[nombre] = (Texto(valor), esLista);
            }
            else if (tipoPropiedad != typeof(string) && typeof(IEnumerable).IsAssignableFrom(tipoPropiedad))
            {
                var elemento = tipoPropiedad.IsArray ? tipoPropiedad.GetElementType()! : tipoPropiedad.GetGenericArguments().Single();
                if (EsSimple(Nullable.GetUnderlyingType(elemento) ?? elemento))
                {
                    esperadas[$"{nombre}__0"] = (string.Empty, true);
                }
                else
                {
                    Expandir(elemento, Activator.CreateInstance(elemento), $"{nombre}__0", esLista: true, esperadas);
                }
            }
            else
            {
                Expandir(tipoPropiedad, valor ?? Activator.CreateInstance(tipoPropiedad), nombre, esLista, esperadas);
            }
        }
    }

    private static bool EsSimple(Type tipo) =>
        tipo.IsPrimitive || tipo.IsEnum || tipo == typeof(string) || tipo == typeof(decimal) || tipo == typeof(TimeSpan) || tipo == typeof(Uri) || tipo == typeof(Guid);

    private static string Texto(object? valor) => valor switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => valor.ToString() ?? string.Empty,
    };

    /// <summary>Clases de opciones de Cotifly (Api, Aplicación e Infraestructura) con <c>public const string Seccion</c>.</summary>
    private static IEnumerable<(Type Tipo, string Seccion)> TiposConSeccion() =>
        new[] { typeof(Program).Assembly, typeof(Cotifly.Aplicacion.Correo.Services.clsOpcionesCorreo).Assembly, typeof(Cotifly.Infraestructura.clsDependenciasInfraestructura).Assembly }
            .SelectMany(a => a.GetTypes())
            .Select(t => (Tipo: t, Campo: t.GetField("Seccion", BindingFlags.Public | BindingFlags.Static)))
            .Where(x => x.Campo is { IsLiteral: true } && x.Campo.FieldType == typeof(string) && x.Tipo.IsClass && !x.Tipo.IsAbstract)
            .Select(x => (x.Tipo, (string)x.Campo!.GetRawConstantValue()!));

    /// <summary>
    /// Tipos de Cotifly que la API enlaza a la configuración: los que tienen un <see cref="IOptionsChangeTokenSource{TOptions}"/> (lo
    /// registran tanto <c>Bind</c> como <c>BindConfiguration</c>), leídos de los servicios reales de la API.
    /// </summary>
    private static HashSet<Type> TiposEnlazados()
    {
        IServiceCollection? capturados = null;
        using var api = new clsCotiflyApiFactory();
        using var factory = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => capturados = s));
        _ = factory.Services;
        return capturados!
            .Where(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(IOptionsChangeTokenSource<>))
            .Select(d => d.ServiceType.GetGenericArguments()[0])
            .Where(t => t.Namespace?.StartsWith("Cotifly.", StringComparison.Ordinal) == true)
            .ToHashSet();
    }

    private static YamlMappingNode Hijo(YamlMappingNode nodo, string clave) =>
        (YamlMappingNode)nodo.Children.Single(p => p.Key is YamlScalarNode { Value: var valor } && valor == clave).Value;

    [GeneratedRegex(@"__\d+(?<fin>__|$)", RegexOptions.CultureInvariant)]
    private static partial Regex IndiceLista();

    [GeneratedRegex(@"^\$\{(?<nombre>[A-Za-z0-9_]+)(?<op>:-|:\?)(?<defecto>.*)\}$", RegexOptions.CultureInvariant)]
    private static partial Regex Referencia();
}
