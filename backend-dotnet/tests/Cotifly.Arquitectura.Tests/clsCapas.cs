using System.Reflection;
using ArchUnitNET.Loader;
using Cotifly.Aplicacion;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura;

namespace Cotifly.Arquitectura.Tests;

/// <summary>
/// Ensamblados de las 4 capas (ADR-002) y el modelo de ArchUnitNET cargado una sola vez.
/// </summary>
internal static class clsCapas
{
    public static readonly Assembly Dominio = typeof(clsErrorDominio).Assembly;
    public static readonly Assembly Aplicacion = typeof(clsDependenciasAplicacion).Assembly;
    public static readonly Assembly Infraestructura = typeof(clsDependenciasInfraestructura).Assembly;
    public static readonly Assembly Api = typeof(Program).Assembly;

    public static readonly Assembly[] Todas = [Dominio, Aplicacion, Infraestructura, Api];

    public static readonly ArchUnitNET.Domain.Architecture Modelo =
        new ArchLoader().LoadAssemblies(Todas).Build();
}
