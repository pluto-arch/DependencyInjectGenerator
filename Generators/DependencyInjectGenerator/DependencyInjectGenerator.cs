﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using DependencyInjectGenerator.SyntaxReceivers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DependencyInjectGenerator
{
    [Generator]
    public class DependencyInjectGenerator:ISourceGenerator
    {
        /// <inheritdoc />
        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new TypeSyntaxReceiver());
        }

        /// <inheritdoc />
        public void Execute(GeneratorExecutionContext context)
        {
            var compilation = context.Compilation;
            var mainMethod = compilation.GetEntryPoint(context.CancellationToken);
            var @namespace = "Microsoft.Extensions.DependencyInjection";

            var syntaxReceiver = (TypeSyntaxReceiver)context.SyntaxReceiver;
            var injectTargets = syntaxReceiver?.TypeDeclarationsWithAttributes;

            if (injectTargets == null||!injectTargets.Any())
            {
                return;
            }

            var injectCodeStr = GeneratorInjectAttributeCode(context, @namespace);

            var options = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;
            var logSyntaxTree = CSharpSyntaxTree.ParseText(injectCodeStr,options);
            compilation = compilation.AddSyntaxTrees(logSyntaxTree);
            var logAttribute = compilation.GetTypeByMetadataName($"{@namespace}.InjectableAttribute");
            var targetTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

            foreach (var targetTypeSyntax in injectTargets)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var semanticModel = compilation.GetSemanticModel(targetTypeSyntax.SyntaxTree);
                var targetType = semanticModel.GetDeclaredSymbol(targetTypeSyntax);
                var hasInjectAttribute = targetType?.GetAttributes().Any(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, logAttribute)) ?? false;
                if (!hasInjectAttribute)
                    continue;

                targetTypes.Add(targetType);

            }

            try
            {
                var injectStr = $@" 
namespace Microsoft.Extensions.DependencyInjection {{
    public static class AutoInjectHelper
    {{
        public static IServiceCollection AutoInject(this IServiceCollection service)
        {{";
                var sb = new StringBuilder(injectStr);

                foreach (var targetType in targetTypes)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    var proxySource = GenerateInjectCode(targetType, @namespace,logAttribute);
                    sb.AppendLine(proxySource);
                }

                var end =$@"  return  service; }}
    }}
}}";
                sb.Append(end);

            
                context.AddSource($"AutoInjectHelper.Inject.cs", sb.ToString());
            }
            catch (Exception e)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        "AUTODI_01",
                        "Log generator",
                        $"生成注入代码失败，{e.Message}",
                        defaultSeverity: DiagnosticSeverity.Error,
                        severity: DiagnosticSeverity.Error,
                        isEnabledByDefault: true,
                        warningLevel: 0));
            }
        }

        private string GenerateInjectCode(ITypeSymbol targetType, string @namespace,INamedTypeSymbol attribute)
        {
            var attributeValue = targetType.GetAttributes().FirstOrDefault(x=>SymbolEqualityComparer.Default.Equals(x.AttributeClass,attribute));
            if (attributeValue==null)
            {
                return null;
            }

            var attrobuitePropertys = attributeValue.ConstructorArguments;
            var typeEnum = attrobuitePropertys.FirstOrDefault(x=>x.Kind==TypedConstantKind.Enum).Value;
            var types = attrobuitePropertys.FirstOrDefault(x => x.Kind==TypedConstantKind.Type).Value as ITypeSymbol;

            if (types==null)
            {
                return AddClassic(targetType,typeEnum);
            }
            else
            {
                return AddWithInterface(targetType,types,typeEnum);
            }

        }

        private string AddWithInterface(ITypeSymbol impl, ITypeSymbol @interface, object typeEnum)
        {
            switch (typeEnum)
            {
                case 0x01:
                    return GetInjectCodeWithInterfact(impl,@interface,InjectCodeGeneratorFactory.EnumInjectLifeTime.Scoped);
                case 0x02:
                    return GetInjectCodeWithInterfact(impl,@interface,InjectCodeGeneratorFactory.EnumInjectLifeTime.Singleton);
                case 0x03:
                    return GetInjectCodeWithInterfact(impl,@interface,InjectCodeGeneratorFactory.EnumInjectLifeTime.Transient);
                default:
                    return null;
            }
        }



        private string GetInjectCodeWithInterfact(ITypeSymbol implType,ITypeSymbol interfaceType,InjectCodeGeneratorFactory.EnumInjectLifeTime lifeTime)
        {
            if (implType == null)
                return null;
            return InjectCodeGeneratorFactory.GetGenerator(lifeTime).GenerateWithInterface((INamedTypeSymbol)implType,(INamedTypeSymbol)interfaceType);
        }


        private string AddClassic(ITypeSymbol name, object typeEnum)
        {
            switch (typeEnum)
            {
                case 0x01:
                    return GetInjectCode(name,InjectCodeGeneratorFactory.EnumInjectLifeTime.Scoped);
                case 0x02:
                    return GetInjectCode(name,InjectCodeGeneratorFactory.EnumInjectLifeTime.Singleton);
                case 0x03:
                    return GetInjectCode(name,InjectCodeGeneratorFactory.EnumInjectLifeTime.Transient);
                default:
                    return null;
            }
        }

        
        private string GetInjectCode(ITypeSymbol implType,InjectCodeGeneratorFactory.EnumInjectLifeTime lifeTime)
        {
            if (implType == null)
                return null;
            return InjectCodeGeneratorFactory.GetGenerator(lifeTime).Generate((INamedTypeSymbol)implType);
        }


        private string GeneratorInjectAttributeCode(GeneratorExecutionContext context, string @namespace)
        {
            var injectCodeStr = $@"
            using System.AttributeUsage;
            namespace {@namespace}
            {{
                public enum InjectLifeTime
                {{
                    Scoped=0x01,
                    Singleton=0x02,
                    Transient=0x03
                }}

                [AttributeUsage(AttributeTargets.Class)]
                public class InjectableAttribute:System.Attribute
                {{ 
                    public InjectableAttribute(InjectLifeTime lifeTime,Type interfactType=null)
                    {{
                        LifeTime = lifeTime;
                        InterfactType=interfactType;
                    }}
		        
                    public InjectLifeTime LifeTime {{get;}}

                    public Type InterfactType {{get;}}
                }}
            }}";

            context.AddSource("AutoInjectAttribute.g.cs", injectCodeStr.Trim());
            return injectCodeStr;
        }

    }
}
